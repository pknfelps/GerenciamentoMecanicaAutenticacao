using Amazon;
using Amazon.Lambda;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Model;
using DotNet.Testcontainers.Builders;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Function;
using GerenciamentoMecanica.Autenticacao.Function.Http;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Email;
using GerenciamentoMecanica.Serverless.EmailFunction.Configuration;
using GerenciamentoMecanica.Serverless.EmailFunction.Smtp;
using Npgsql;
using NSubstitute;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmailHandler = GerenciamentoMecanica.Serverless.EmailFunction.FunctionHandler;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

public sealed partial class PostgreSqlCustomerAccessStoreTests
{
    private const string TestKey = "local-test-signing-key-with-more-than-32-characters";
    private FunctionRuntime HttpRuntime(ICustomerCodeSender sender)
    {
        var settings = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Pooling = false,
            SslMode = SslMode.Disable,
            Username = "access_auth",
            Password = "local-tests-only-auth",
            ApplicationName = "ae3-access-tests",
            Timeout = 5,
            CommandTimeout = 5
        };
        return new FunctionRuntime(new RuntimeConfiguration(new JwtConfiguration(TestKey, "test-issuer", "test-audience"),
            new PostgreSqlConfiguration(settings)), sender: sender, logger: logger);
    }
    private static ILambdaContext HttpContext(double seconds = 15)
    {
        var context = Substitute.For<ILambdaContext>();
        context.RemainingTime.Returns(TimeSpan.FromSeconds(seconds));
        context.Logger.Returns(Substitute.For<ILambdaLogger>());
        context.AwsRequestId.Returns("local-http-request");
        return context;
    }
    private static APIGatewayProxyRequest CodeRequest(string document = Document) => new()
    {
        HttpMethod = "POST",
        Path = AuthenticationRoute.RequestCode,
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
        Body = JsonSerializer.Serialize(new { document })
    };
    private static APIGatewayProxyRequest Verification(Guid id, string code, string document = Document)
    {
        var request = CodeRequest(document);
        request.Path = AuthenticationRoute.VerifyCode;
        request.Body = JsonSerializer.Serialize(new { challengeId = id, document, code });
        return request;
    }
    private static Guid ChallengeFrom(APIGatewayProxyResponse response)
    {
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.GetProperty("challengeId").GetGuid();
    }

    [Test]
    public async Task HttpFlowPersistsBeforeCaptureThenConsumesOnlyOnceAndKeepsJwtClaims()
    {
        string code = "";
        senderCapture = Substitute.For<ICustomerCodeSender>();
        senderCapture.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            code = call.ArgAt<string>(2);
            Assert.That(await ScalarAsync<string>("SELECT state FROM customer_auth_challenges"), Is.EqualTo("Pending"));
            Assert.That(await ScalarAsync<int>("SELECT count(*) FROM pg_stat_activity WHERE application_name='ae3-access-tests' AND state='idle in transaction'"), Is.Zero);
        });
        var initializations = 0;
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ =>
        { initializations++; return Task.FromResult(HttpRuntime(senderCapture)); }));
        var request = await handler.HandleAsync(CodeRequest(), HttpContext());
        Assert.That(request.StatusCode, Is.EqualTo(202));
        var id = ChallengeFrom(request);
        Assert.That((await ChallengeAsync(id)).State, Is.EqualTo("Ready"));
        Assert.That((await handler.HandleAsync(CodeRequest(), HttpContext())).StatusCode, Is.EqualTo(429));
        var outcomes = await Task.WhenAll(handler.HandleAsync(Verification(id, code), HttpContext()), handler.HandleAsync(Verification(id, code), HttpContext()));
        Assert.That(outcomes.Select(result => result.StatusCode).Order(), Is.EqualTo(new[] { 200, 401 }));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(JsonSerializer.Deserialize<string>(outcomes.Single(result => result.StatusCode == 200).Body));
        Assert.That(jwt.Claims.Any(claim => claim.Value == CustomerId.ToString()), Is.True);
        Assert.That(jwt.Claims.Any(claim => claim.Value == "Customer"), Is.True);
        Assert.That(jwt.ValidTo - DateTime.UtcNow, Is.InRange(TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(10.1)));
        Assert.That(jwt.Claims.Any(claim => claim.Value == Document || claim.Value == code || claim.Type is "email" or "order_id" or "challengeId"), Is.False);
        Assert.That(initializations, Is.EqualTo(1));
        Assert.That((await ChallengeAsync(id)).State, Is.EqualTo("Consumed"));
        await senderCapture.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
    private ICustomerCodeSender senderCapture = null!;

    [TestCase("missing")]
    [TestCase("inactive")]
    [TestCase("email")]
    public async Task HttpRequestForIneligibleCustomerWritesNothingAndNeverSends(string reason)
    {
        if (reason == "missing") await SqlAsync("DELETE FROM customers WHERE id=@CustomerId", new { CustomerId });
        if (reason == "inactive") await SqlAsync("UPDATE customers SET status='Inactive' WHERE id=@CustomerId", new { CustomerId });
        if (reason == "email") await SqlAsync("UPDATE customers SET email='invalid-address' WHERE id=@CustomerId", new { CustomerId });
        var sender = Substitute.For<ICustomerCodeSender>();
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime(sender))));
        var response = await handler.HandleAsync(CodeRequest(), HttpContext());
        Assert.That(response.StatusCode, Is.EqualTo(202));
        Assert.That(ChallengeFrom(response), Is.Not.EqualTo(Guid.Empty));
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_challenges"), Is.Zero);
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_request_limits"), Is.Zero);
        Assert.That(sender.ReceivedCalls(), Is.Empty);
        if (reason == "missing") Assert.That(logger.ReceivedCalls().Any(call => call.GetArguments().Any(value => value?.ToString() == "Solicitação de autenticação para cliente não encontrado.")), Is.True);
    }

    [Test]
    public async Task HttpSendFailureInvalidatesButDoesNotRefundTheLimit()
    {
        var sender = Substitute.For<ICustomerCodeSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new EmailInvocationException()));
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime(sender))));
        var response = await handler.HandleAsync(CodeRequest(), HttpContext());
        Assert.That(response.StatusCode, Is.EqualTo(500));
        Assert.That(await ScalarAsync<string>("SELECT state FROM customer_auth_challenges"), Is.EqualTo("Invalidated"));
        Assert.That((await LimitAsync()).RequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ValidButDifferentDocumentSpendsAnAttemptAndInactiveCustomerCannotConfirm()
    {
        string code = "";
        var sender = Substitute.For<ICustomerCodeSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => { code = call.ArgAt<string>(2); return Task.CompletedTask; });
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime(sender))));
        var id = ChallengeFrom(await handler.HandleAsync(CodeRequest(), HttpContext()));
        Assert.That((await handler.HandleAsync(Verification(id, code, OtherDocument), HttpContext())).StatusCode, Is.EqualTo(401));
        Assert.That((await ChallengeAsync(id)).FailedAttempts, Is.EqualTo(1));
        await SqlAsync("UPDATE customers SET status='Inactive' WHERE id=@CustomerId", new { CustomerId });
        Assert.That((await handler.HandleAsync(Verification(id, code), HttpContext())).StatusCode, Is.EqualTo(401));
        Assert.That((await ChallengeAsync(id)).State, Is.EqualTo("Invalidated"));
    }

    [Test]
    public async Task SqlFailureIsTechnicalAndClosesTheConnection()
    {
        await SqlAsync("REVOKE SELECT ON customer_auth_challenges FROM access_auth");
        var sender = Substitute.For<ICustomerCodeSender>();
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime(sender))));
        var result = await handler.HandleAsync(Verification(Guid.NewGuid(), Code), HttpContext());
        Assert.That(result.StatusCode, Is.EqualTo(500));
        Assert.That(result.Body, Does.Not.Contain("permission").And.Not.Contain("customer_auth_challenges"));
    }

    [Test]
    public async Task HttpDeadlineCancelsBlockedPostgreSqlQuery()
    {
        await using var blocker = await admin.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await Dapper.SqlMapper.ExecuteAsync(blocker, "LOCK TABLE customers IN ACCESS EXCLUSIVE MODE", transaction: transaction);
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime(Substitute.For<ICustomerCodeSender>()))));
        var response = await handler.HandleAsync(CodeRequest(), HttpContext(1.2));
        Assert.That(response.StatusCode, Is.EqualTo(500));
        await transaction.RollbackAsync();
        // PostgreSQL observes socket closure asynchronously after the caller deadline.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await ScalarAsync<int>("SELECT count(*) FROM pg_stat_activity WHERE application_name='ae3-access-tests'") == 0)
                return;
            await Task.Delay(20);
        }
        Assert.Fail("Canceled authentication connection remained open.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HandlerDisposalClosesTheCachedDataSource(bool asynchronous)
    {
        var runtime = HttpRuntime(Substitute.For<ICustomerCodeSender>());
        var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(runtime)));
        try
        {
            // Well-formed verification of an absent challenge initializes without sending or writing.
            Assert.That((await handler.HandleAsync(Verification(Guid.NewGuid(), Code), HttpContext())).StatusCode, Is.EqualTo(401));
            if (asynchronous) await handler.DisposeAsync();
            else handler.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await runtime.Database.OpenConnectionAsync());
        }
        finally
        {
            await handler.DisposeAsync();
        }
    }

    [Test]
    public async Task InitializationCanRetryAndConcurrentCallsReuseOneSuccessfulRuntime()
    {
        var count = 0;
        await using var provider = new FunctionRuntimeProvider(async token =>
        {
            if (Interlocked.Increment(ref count) == 1) throw new IOException("test-only-initialization-failure");
            await Task.Delay(25, token);
            return HttpRuntime(Substitute.For<ICustomerCodeSender>());
        });
        Assert.ThrowsAsync<IOException>(() => provider.VerifyCodeAsync(Guid.NewGuid(), Document, Code, CancellationToken.None));
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => provider.VerifyCodeAsync(Guid.NewGuid(), Document, Code, CancellationToken.None)));
        Assert.That(results.All(result => result.Outcome == CustomerAuthenticationOutcome.NotEligible), Is.True);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task CanceledInitializationDisposesTheAbandonedRuntimeAndAllowsRetry()
    {
        var abandoned = HttpRuntime(Substitute.For<ICustomerCodeSender>());
        using var cancellation = new CancellationTokenSource();
        var initializations = 0;
        await using var provider = new FunctionRuntimeProvider(_ =>
        {
            initializations++;
            if (initializations != 1) return Task.FromResult(HttpRuntime(Substitute.For<ICustomerCodeSender>()));
            cancellation.Cancel();
            return Task.FromResult(abandoned);
        });
        Assert.CatchAsync<OperationCanceledException>(() => provider.VerifyCodeAsync(Guid.NewGuid(), Document, Code, cancellation.Token));
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await abandoned.Database.OpenConnectionAsync());
        Assert.That((await provider.VerifyCodeAsync(Guid.NewGuid(), Document, Code, CancellationToken.None)).Outcome,
            Is.EqualTo(CustomerAuthenticationOutcome.NotEligible));
        Assert.That(initializations, Is.EqualTo(2));
    }

    [Test]
    public async Task RequestVerifyFlowUsesTheRealEmailHandlerAndSmtpCapture()
    {
        const string configuration = """
        {"ServerOptions":{"Urls":"http://*:8080","Port":2525,"Database":"","LockSettings":true,
        "TlsMode":"None","AuthenticationRequired":false,"AllowRemoteConnections":true,"ImapPort":null,"Pop3Port":null,"Mailboxes":[],"Users":[],"DeliverToStdout":""},
        "RelayOptions":{"SmtpServer":"","AutomaticEmails":[],"AutomaticRelayExpression":""},"Serilog":{"MinimumLevel":{"Default":"Fatal"}}}
        """;
        await using var smtp = new ContainerBuilder("rnwood/smtp4dev:3.15.0")
            .WithWorkingDirectory("/app").WithResourceMapping(Encoding.UTF8.GetBytes(configuration), "/app/appsettings.json")
            .WithEnvironment("SERVEROPTIONS__URLS", "http://*:8080")
            .WithTmpfsMount("/smtp4dev").WithPortBinding(2525, true).WithPortBinding(8080, true)
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var bindings in (parameters.HostConfig?.PortBindings ?? throw new InvalidOperationException("Docker ports unavailable.")).Values)
                    foreach (var binding in bindings ?? []) binding.HostIP = "127.0.0.1";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/")))
            .Build();
        await smtp.StartAsync();
        var settings = SmtpSettings.Load(key => key switch
        {
            "Email__SmtpHost" => smtp.Hostname,
            "Email__SmtpPort" => smtp.GetMappedPublicPort(2525).ToString(),
            _ => null
        });
        var emailHandler = new EmailHandler(() => new SmtpEmailSender(settings));
        var emailLogger = Substitute.For<ILambdaLogger>();
        var emailContext = HttpContext(6);
        emailContext.Logger.Returns(emailLogger);
        var lambda = Substitute.For<IAmazonLambda>();
        lambda.InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var response = await emailHandler.HandleAsync(call.Arg<InvokeRequest>().PayloadStream, emailContext);
            return new InvokeResponse { StatusCode = 200, Payload = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(response)) };
        });
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime(new LambdaCustomerCodeSender(lambda, "local-email-alias")))));
        var authContext = HttpContext();
        var request = await handler.HandleAsync(CodeRequest(), authContext);
        Assert.That(request.StatusCode, Is.EqualTo(202));
        using var http = new HttpClient { BaseAddress = new Uri($"http://{smtp.Hostname}:{smtp.GetMappedPublicPort(8080)}/") };
        string? messageId = null;
        for (var attempt = 0; attempt < 60 && messageId is null; attempt++)
        {
            using var box = JsonDocument.Parse(await http.GetStringAsync("api/Messages"));
            if (box.RootElement.GetProperty("rowCount").GetInt32() == 1)
            {
                var message = box.RootElement.GetProperty("results")[0];
                Assert.That(message.GetProperty("isRelayed").GetBoolean(), Is.False);
                messageId = message.GetProperty("id").GetString();
            }
            else await Task.Delay(50);
        }
        Assert.That(messageId, Is.Not.Null);
        using var rawRequest = new HttpRequestMessage(HttpMethod.Get, $"api/Messages/{messageId}/raw");
        rawRequest.Headers.Accept.ParseAdd("message/rfc822");
        using var rawResponse = await http.SendAsync(rawRequest);
        rawResponse.EnsureSuccessStatusCode();
        var source = await rawResponse.Content.ReadAsStringAsync();
        using var mimeStream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var mime = await MimeKit.MimeMessage.LoadAsync(mimeStream);
        var code = Regex.Match(mime.TextBody ?? throw new AssertionException("Text body missing."), @"\b[0-9]{8}\b").Value;
        Assert.That(code, Has.Length.EqualTo(8));
        var verified = await handler.HandleAsync(Verification(ChallengeFrom(request), code), authContext);
        Assert.That(verified.StatusCode, Is.EqualTo(200));
        var logs = authContext.Logger.ReceivedCalls().Concat(emailLogger.ReceivedCalls()).Select(call => call.GetArguments()[0]?.ToString());
        Assert.That(string.Join("\n", logs), Does.Not.Contain(code).And.Not.Contain(Document).And.Not.Contain("customer@example.invalid").And.Not.Contain("jwt"));
        await lambda.Received(1).InvokeAsync(Arg.Any<InvokeRequest>(), Arg.Any<CancellationToken>());
    }
}

