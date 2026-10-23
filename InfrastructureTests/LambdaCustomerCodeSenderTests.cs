using Amazon.Lambda;
using Amazon.Lambda.Model;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Email;
using NSubstitute;
using System.Text;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

public class LambdaCustomerCodeSenderTests
{
    private const string Arn = "arn:aws:lambda:us-east-1:121754142617:function:mecanica-hom-email:live";
    private IAmazonLambda lambda = null!;
    private LambdaCustomerCodeSender sender = null!;
    [SetUp]
    public void Setup()
    {
        lambda = Substitute.For<IAmazonLambda>();
        sender = new LambdaCustomerCodeSender(lambda, Arn);
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Response("{\"version\":1,\"outcome\":\"captured\"}"));
    }
    private static InvokeResponse Response(string json, int status = 200, string? functionError = null) => new()
    { StatusCode = status, FunctionError = functionError, Payload = new MemoryStream(Encoding.UTF8.GetBytes(json)) };

    [Test]
    public async Task SendsOneSynchronousRequestWithNoLogsAndEncodedHtml()
    {
        byte[]? sent = null;
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<InvokeRequest>();
            Assert.That(request.FunctionName, Is.EqualTo(Arn));
            Assert.That(request.InvocationType, Is.EqualTo(InvocationType.RequestResponse));
            Assert.That(request.LogType, Is.EqualTo(LogType.None));
            sent = request.PayloadStream.ToArray();
            return Response("{\"version\":1,\"outcome\":\"captured\"}");
        });
        await sender.SendAsync("Cliente <b>&", "customer@example.invalid", "01234567", CancellationToken.None);
        using var json = JsonDocument.Parse(sent!);
        Assert.That(json.RootElement.GetProperty("htmlBody").GetString(), Does.Contain("&lt;b&gt;&amp;").And.Not.Contain("Cliente <b>"));
        Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("customer_access_code"));
        Assert.That(json.RootElement.EnumerateObject().Count(), Is.EqualTo(7));
        await lambda.Received(1).InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>());
    }

    [TestCase(202, null)]
    [TestCase(200, "Unhandled")]
    public void TransportStatusOrFunctionErrorCannotConfirmCapture(int status, string? functionError)
    {
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Response("{\"version\":1,\"outcome\":\"captured\"}", status, functionError));
        Assert.ThrowsAsync<EmailInvocationException>(() => sender.SendAsync("Cliente", "customer@example.invalid", "01234567", CancellationToken.None));
    }

    [TestCase("")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{")]
    [TestCase("{\"version\":2,\"outcome\":\"captured\"}")]
    [TestCase("{\"version\":1,\"outcome\":\"captured\",\"error\":\"sensitive\"}")]
    [TestCase("{\"version\":1,\"version\":1,\"outcome\":\"captured\"}")]
    [TestCase("{\"version\":\"1\",\"outcome\":\"captured\"}")]
    [TestCase("{\"version\":1,\"outcome\":\"failure\",\"error\":\"smtp-unavailable\"}")]
    public void InvalidOrFailedResponseNeverBecomesCapture(string payload)
    {
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Response(payload));
        var error = Assert.ThrowsAsync<EmailInvocationException>(() => sender.SendAsync("Cliente", "customer@example.invalid", "01234567", CancellationToken.None));
        Assert.That(error!.Message, Does.Not.Contain("sensitive"));
        Assert.That(error.InnerException, Is.Null);
    }

    [Test]
    public void KnownRemoteTimeoutRemainsATimeoutWithFixedMessage()
    {
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Response("{\"version\":1,\"outcome\":\"failure\",\"error\":\"timeout\"}"));
        Assert.ThrowsAsync<TimeoutException>(() => sender.SendAsync("Cliente", "customer@example.invalid", "01234567", CancellationToken.None));
    }

    [Test]
    public async Task CallerCancellationStopsInvokeWithoutRetryOrLeakingSdkContent()
    {
        var observed = CancellationToken.None;
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        { observed = call.Arg<CancellationToken>(); await Task.Delay(Timeout.Infinite, observed); return Response(""); });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var error = Assert.CatchAsync<OperationCanceledException>(() => sender.SendAsync("Cliente", "customer@example.invalid", "01234567", cancellation.Token));
        Assert.That(observed.IsCancellationRequested, Is.True);
        Assert.That(error!.InnerException, Is.Null);
        await lambda.Received(1).InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void SdkFailureIsSanitizedAndNotRetriedByTheAdapter()
    {
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<InvokeResponse>(new AmazonLambdaException("secret-in-sdk-message")));
        var error = Assert.ThrowsAsync<EmailInvocationException>(() => sender.SendAsync("Cliente", "customer@example.invalid", "01234567", CancellationToken.None));
        Assert.That(error!.Message, Does.Not.Contain("secret-in-sdk-message"));
        Assert.That(error.InnerException, Is.Null);
    }

    [Test]
    public void ClientDisablesRetriesAndPayloadLogging()
    {
        var config = EmailInvocationConfiguration.ClientConfiguration();
        Assert.That(config.MaxErrorRetry, Is.Zero);
        Assert.That(config.LogResponse, Is.False);
        Assert.That(config.LogMetrics, Is.False);
        Assert.That(config.Timeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [TestCase("hom")]
    [TestCase("prd")]
    public void DiscoveryAllowsOnlyTheEnvironmentAliasAndContractOne(string environment)
    {
        var runtime = RuntimeEnvironment.Parse(environment, "us-east-1");
        var arn = Arn.Replace("-hom-", "-" + environment + "-");
        Assert.That(EmailInvocationConfiguration.Parse(runtime, arn, "1").FunctionArn, Is.EqualTo(arn));
        Assert.Throws<InvalidOperationException>(() => EmailInvocationConfiguration.Parse(runtime, arn.Replace(":live", ":7"), "1"));
        Assert.Throws<InvalidOperationException>(() => EmailInvocationConfiguration.Parse(runtime, arn, "2"));
    }
}
