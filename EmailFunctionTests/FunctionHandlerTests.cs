using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using GerenciamentoMecanica.Serverless.EmailFunction;
using GerenciamentoMecanica.Serverless.EmailFunction.Contracts;
using GerenciamentoMecanica.Serverless.EmailFunction.Smtp;
using MailKit.Net.Smtp;
using NSubstitute;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EmailFunctionTests;

public sealed class FunctionHandlerTests
{
    private const string Sensitive = "validation-only-sensitive-message-password-code";

    private static string Json(EmailResponse response)
    {
        using var output = new MemoryStream();
        new DefaultLambdaJsonSerializer().Serialize(response, output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string SingleLog(ILambdaLogger logger) =>
        (string)logger.ReceivedCalls().Single().GetArguments()[0]!;

    [Test]
    public async Task CapturedResponseUsesTheExactVersionedSchemaAndOneSafeLog()
    {
        var sender = Substitute.For<ISmtpEmailSender>();
        var logger = Substitute.For<ILambdaLogger>();
        var handler = new FunctionHandler(() => sender);
        using var payload = EmailTestData.Payload();
        var response = await handler.HandleAsync(payload, EmailTestData.Context(logger));

        Assert.That(Json(response), Is.EqualTo("{\"version\":1,\"outcome\":\"captured\"}"));
        await sender.Received(1).SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>());
        using var log = JsonDocument.Parse(SingleLog(logger));
        Assert.Multiple(() =>
        {
            Assert.That(log.RootElement.GetProperty("message").GetString(), Is.EqualTo("Processamento de envio de e-mail concluído."));
            Assert.That(log.RootElement.GetProperty("lambdaRequestId").GetString(), Is.EqualTo("email-request-id"));
            Assert.That(log.RootElement.GetProperty("type").GetString(), Is.EqualTo("customer_access_code"));
            Assert.That(log.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("captured"));
            Assert.That(log.RootElement.GetProperty("durationMs").GetInt64(), Is.GreaterThanOrEqualTo(0));
            Assert.That(SingleLog(logger), Does.Not.Contain("customer@example.invalid").And.Not.Contain("01234567").And.Not.Contain("textBody"));
        });
    }

    [Test]
    public async Task InvalidPayloadDoesNotInitializeTheSenderOrEchoItsValues()
    {
        var initialized = false;
        var logger = Substitute.For<ILambdaLogger>();
        var handler = new FunctionHandler(() => { initialized = true; throw new InvalidOperationException(Sensitive); });
        var message = EmailTestData.Message();
        message["type"] = Sensitive;
        using var payload = EmailTestData.Payload(message);
        var response = await handler.HandleAsync(payload, EmailTestData.Context(logger));

        Assert.Multiple(() =>
        {
            Assert.That(response.Error, Is.EqualTo("invalid-request"));
            Assert.That(initialized, Is.False);
            Assert.That(SingleLog(logger), Does.Not.Contain(Sensitive).And.Not.Contain("customer@example.invalid"));
        });
    }

    [TestCase("smtp", "smtp-unavailable")]
    [TestCase("protocol", "smtp-unavailable")]
    [TestCase("timeout", "timeout")]
    [TestCase("cancellation", "timeout")]
    [TestCase("unexpected", "technical-failure")]
    public async Task FailureResponseAndLogNeverContainTheOriginalException(string kind, string expectedError)
    {
        Exception error = kind switch
        {
            "smtp" => new IOException(Sensitive),
            "protocol" => new SmtpProtocolException(Sensitive),
            "timeout" => new TimeoutException(Sensitive),
            "cancellation" => new OperationCanceledException(Sensitive),
            _ => new InvalidOperationException(Sensitive, new Exception(Sensitive))
        };
        var sender = Substitute.For<ISmtpEmailSender>();
        sender.SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(error));
        var logger = Substitute.For<ILambdaLogger>();
        var handler = new FunctionHandler(() => sender);
        using var payload = EmailTestData.Payload();
        var response = await handler.HandleAsync(payload, EmailTestData.Context(logger));
        using var log = JsonDocument.Parse(SingleLog(logger));

        Assert.Multiple(() =>
        {
            Assert.That(response.Outcome, Is.EqualTo("failure"));
            Assert.That(response.Error, Is.EqualTo(expectedError));
            Assert.That(log.RootElement.GetProperty("exceptionType").GetString(), Is.EqualTo(error.GetType().Name));
            Assert.That(Json(response) + SingleLog(logger), Does.Not.Contain(Sensitive).And.Not.Contain("stackTrace").And.Not.Contain("innerException"));
        });
        await sender.Received(1).SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InputReadFailureIsTechnicalAndDoesNotStartSmtp()
    {
        var sender = Substitute.For<ISmtpEmailSender>();
        var stream = Substitute.For<Stream>();
        stream.ReadAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromException<int>(new IOException(Sensitive)));
        var handler = new FunctionHandler(() => sender);
        var response = await handler.HandleAsync(stream, EmailTestData.Context());
        Assert.That(response.Error, Is.EqualTo("technical-failure"));
        await sender.DidNotReceive().SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReusesTheConfiguredSenderButSendsOncePerInvocation()
    {
        var initializations = 0;
        var sender = Substitute.For<ISmtpEmailSender>();
        var handler = new FunctionHandler(() => { initializations++; return sender; });
        using var first = EmailTestData.Payload();
        using var second = EmailTestData.Payload();
        await handler.HandleAsync(first, EmailTestData.Context());
        await handler.HandleAsync(second, EmailTestData.Context());
        Assert.That(initializations, Is.EqualTo(1));
        await sender.Received(2).SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidConfigurationIsSanitizedInsteadOfEscapingTheHandler()
    {
        var handler = new FunctionHandler(() => throw new InvalidOperationException(Sensitive));
        var logger = Substitute.For<ILambdaLogger>();
        using var payload = EmailTestData.Payload();
        var response = await handler.HandleAsync(payload, EmailTestData.Context(logger));
        Assert.That(response.Error, Is.EqualTo("technical-failure"));
        Assert.That(SingleLog(logger), Does.Not.Contain(Sensitive));
    }

    [Test]
    public async Task NoResponseMarginPreventsSmtpFromStarting()
    {
        var initialized = false;
        var handler = new FunctionHandler(() => { initialized = true; return Substitute.For<ISmtpEmailSender>(); });
        using var payload = EmailTestData.Payload();
        var response = await handler.HandleAsync(payload, EmailTestData.Context(remaining: TimeSpan.FromMilliseconds(999)));
        Assert.That(response.Error, Is.EqualTo("timeout"));
        Assert.That(initialized, Is.False);
    }

    [Test]
    public async Task RemainingLambdaTimeShortensTheSharedBudgetAndCancelsTheSender()
    {
        var observed = CancellationToken.None;
        var sender = Substitute.For<ISmtpEmailSender>();
        sender.SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            return Task.Delay(Timeout.Infinite, observed);
        });
        var handler = new FunctionHandler(() => sender);
        using var payload = EmailTestData.Payload();
        var elapsed = Stopwatch.StartNew();
        var response = await handler.HandleAsync(payload, EmailTestData.Context(remaining: TimeSpan.FromMilliseconds(1100)));
        Assert.That(response.Error, Is.EqualTo("timeout"));
        Assert.That(observed.IsCancellationRequested, Is.True);
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task MoreLambdaTimeDoesNotGiveSmtpMoreThanThreeSeconds()
    {
        var sender = Substitute.For<ISmtpEmailSender>();
        sender.SendAsync(Arg.Any<EmailRequest>(), Arg.Any<CancellationToken>()).Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        var handler = new FunctionHandler(() => sender);
        using var payload = EmailTestData.Payload();
        var elapsed = Stopwatch.StartNew();
        var response = await handler.HandleAsync(payload, EmailTestData.Context(remaining: TimeSpan.FromSeconds(15)));
        Assert.That(response.Error, Is.EqualTo("timeout"));
        Assert.That(elapsed.Elapsed, Is.InRange(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(6)));
    }
}
