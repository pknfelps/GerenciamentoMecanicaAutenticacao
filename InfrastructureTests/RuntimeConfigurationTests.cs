using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using GerenciamentoMecanica.Autenticacao.Function;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using Npgsql;
using NSubstitute;
using System.Data;
using System.Diagnostics;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

[NonParallelizable]
public class RuntimeConfigurationTests
{
    private const string FakeKey = "test-only-signing-key-at-least-32-characters";
    private const string FakePassword = "test-only-password;with=builder-escaping";
    private IAmazonSimpleSystemsManagement parameters = null!;
    private IAmazonSecretsManager secrets = null!;
    private GetParametersResponse response = null!;
    private string? jwtJson;
    private string? databaseJson;
    private string environment = null!;
    private string certificate = null!;

    [SetUp]
    public void SetUp() => Configure("hom");

    private void Configure(string name)
    {
        environment = name;
        parameters = Substitute.For<IAmazonSimpleSystemsManagement>();
        secrets = Substitute.For<IAmazonSecretsManager>();
        certificate = Path.Combine(AppContext.BaseDirectory, "Certificates", "rds-ca.pem");
        jwtJson = JsonSerializer.Serialize(new { key = FakeKey });

        databaseJson = JsonSerializer.Serialize(new
        {
            username = "mecanica_auth",
            password = FakePassword,
            host = "old.invalid",
            port = 9999,
            dbname = "old_database"
        });

        var values = new Dictionary<string, string>
        {
            ["email/v1/invocation-arn"] = $"arn:aws:lambda:us-east-1:121754142617:function:mecanica-{name}-email:live",
            ["email/v1/contract-version"] = "1",
            ["base/v2/jwt-secret-arn"] = SecretArn("base/jwt"),
            ["base/v2/jwt-issuer"] = $"mecanica-{name}-auth",
            ["base/v2/jwt-audience"] = $"mecanica-{name}-api",
            ["database/v2/endpoint"] = "127.0.0.1",
            ["database/v2/port"] = "5432",
            ["database/v2/database-name"] = "mecanica",
            ["database/v2/auth-secret-arn"] = SecretArn("database/auth"),
            ["database/v2/auth-db-user"] = "mecanica_auth",
            ["database/v2/ssl-mode"] = "verify-full"
        };

        response = new GetParametersResponse
        {
            Parameters = values.Select(pair => new Parameter
            {
                Name = $"/mecanica/{name}/{pair.Key}",
                Value = pair.Value,
                Type = ParameterType.String
            }).ToList()
        };

        parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(response));

        secrets.GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new GetSecretValueResponse
            {
                SecretString = call.Arg<GetSecretValueRequest>().SecretId == SecretArn("base/jwt")
                    ? jwtJson
                    : databaseJson
            }));
    }

    private string SecretArn(string name) =>
        $"arn:aws:secretsmanager:us-east-1:121754142617:secret:/mecanica/{environment}/{name}-aB1234";

    private Parameter Parameter(string field) => response.Parameters.Single(parameter =>
        parameter.Name == $"/mecanica/{environment}/{field}");

    private Task<RuntimeConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
        new AwsRuntimeConfigurationLoader(parameters, secrets)
            .LoadAsync(RuntimeEnvironment.Parse(environment, "us-east-1"), certificate, cancellationToken);

    private FunctionBootstrap Bootstrap(
        string? name = "hom", string? region = "us-east-1",
        Func<RegionEndpoint, IAmazonSimpleSystemsManagement>? parameterFactory = null,
        Func<RegionEndpoint, IAmazonSecretsManager>? secretFactory = null) => new(
        key => key == "Runtime__Environment" ? name : region,
        parameterFactory ?? (_ => parameters), secretFactory ?? (_ => secrets));

    private static void AssertSanitized(Exception error)
    {
        Assert.Multiple(() =>
        {
            Assert.That(error.InnerException, Is.Null);
            Assert.That(error.ToString(), Does.Not.Contain(FakePassword));
            Assert.That(error.ToString(), Does.Not.Contain(FakeKey));
            Assert.That(error.ToString(), Does.Not.Contain("old_database"));
        });
    }

    [Test]
    public async Task SuccessfulBootstrapCountsTwoParametersBatchesAndTwoSecrets()
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var loader = new AwsRuntimeConfigurationLoader(parameters, secrets, metrics);

        await loader.LoadAsync(RuntimeEnvironment.Parse("hom", "us-east-1"), certificate);

        Assert.That(listener.Measurements.Select(point => point["integration"]),
            Is.EqualTo(new[] { "ssm", "ssm", "secretsmanager", "secretsmanager" }));
        Assert.That(listener.Measurements.All(point => Equals(point["outcome"], "success")), Is.True);
    }

    [Test]
    public void IncompleteParametersDoNotCountAsAnSdkTransportFailure()
    {
        response.Parameters.Clear();
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var loader = new AwsRuntimeConfigurationLoader(parameters, secrets, metrics);

        Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            RuntimeEnvironment.Parse("hom", "us-east-1"), certificate));

        Assert.That(listener.Measurements.Single()["outcome"], Is.EqualTo("success"));
    }

    [Test]
    public void SecretTimeoutIsMeasuredBeforeTheExceptionIsSanitized()
    {
        secrets.GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetSecretValueResponse>(new TimeoutException("private-sdk-value")));
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var loader = new AwsRuntimeConfigurationLoader(parameters, secrets, metrics);

        var error = Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(
            RuntimeEnvironment.Parse("hom", "us-east-1"), certificate));

        Assert.Multiple(() =>
        {
            Assert.That(listener.Measurements.Select(point => point["outcome"]), Is.EqualTo(new[] { "success", "success", "timeout" }));
            Assert.That(error!.Message, Does.Not.Contain("private-sdk-value"));
        });
    }

    [Test]
    public async Task ReusedRuntimeDoesNotRepeatBootstrapMetrics()
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var bootstrap = new FunctionBootstrap(key => key == "Runtime__Environment" ? "hom" : "us-east-1",
            _ => parameters, _ => secrets, metrics);
        await using var runtime = new FunctionRuntimeProvider(bootstrap.InitializeAsync);

        await runtime.RequestCodeAsync("invalid-document", CancellationToken.None);
        await runtime.RequestCodeAsync("invalid-document", CancellationToken.None);

        Assert.That(listener.Measurements.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task FailedInitializationCanRetryWithoutDuplicatingTheSuccessfulRuntime()
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var bootstrap = new FunctionBootstrap(key => key == "Runtime__Environment" ? "hom" : "us-east-1",
            _ => parameters, _ => secrets, metrics);
        await using var runtime = new FunctionRuntimeProvider(bootstrap.InitializeAsync);
        parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetParametersResponse>(new InvalidOperationException("private-error")));
        Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RequestCodeAsync("invalid-document", CancellationToken.None));
        parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        await runtime.RequestCodeAsync("invalid-document", CancellationToken.None);
        await runtime.RequestCodeAsync("invalid-document", CancellationToken.None);

        Assert.That(listener.Measurements.Select(point => point["outcome"]), Is.EqualTo(new[] { "failure", "success", "success", "success", "success" }));
    }

    [TestCase("hom")]
    [TestCase("prd")]
    public async Task LoadsCurrentSecretsAndBuildsSecureConnection(string name)
    {
        Configure(name);
        var configuration = await LoadAsync();
        await using var dataSource = configuration.Database.CreateDataSource();
        await using var connection = dataSource.CreateConnection();
        var builder = new NpgsqlConnectionStringBuilder(configuration.Database.ConnectionString);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.Jwt.Key, Is.EqualTo(FakeKey));
            Assert.That(configuration.Jwt.Issuer, Is.EqualTo($"mecanica-{name}-auth"));
            Assert.That(configuration.Jwt.Audience, Is.EqualTo($"mecanica-{name}-api"));
            Assert.That(builder.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(builder.Port, Is.EqualTo(5432));
            Assert.That(builder.Database, Is.EqualTo("mecanica"));
            Assert.That(builder.Username, Is.EqualTo("mecanica_auth"));
            Assert.That(builder.Password, Is.EqualTo(FakePassword));
            Assert.That(builder.SslMode, Is.EqualTo(SslMode.VerifyFull));
            Assert.That(builder.GssEncryptionMode, Is.EqualTo(GssEncryptionMode.Disable));
            Assert.That(builder.Pooling, Is.False);
            Assert.That(builder.Timeout, Is.EqualTo(5));
            Assert.That(builder.CommandTimeout, Is.EqualTo(5));
            Assert.That(builder.RootCertificate, Is.EqualTo(certificate));
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
            Assert.That(connection.ConnectionString, Does.Not.Contain(FakePassword));
        });

        await parameters.Received(1).GetParametersAsync(Arg.Is<GetParametersRequest>(request =>
                request.WithDecryption == false && request.Names.Count == 9 && request.Names.All(name => !name.Contains("/email/"))),
            Arg.Any<CancellationToken>());

        await secrets.Received(1).GetSecretValueAsync(Arg.Is<GetSecretValueRequest>(request =>
                request.SecretId == SecretArn("base/jwt") && request.VersionStage == "AWSCURRENT"),
            Arg.Any<CancellationToken>());

        await secrets.Received(1).GetSecretValueAsync(Arg.Is<GetSecretValueRequest>(request =>
                request.SecretId == SecretArn("database/auth") && request.VersionStage == "AWSCURRENT"),
            Arg.Any<CancellationToken>());

        foreach (var value in new object[] { configuration, configuration.Jwt, configuration.Database })
        {
            Assert.That(value.ToString(), Does.Not.Contain(FakeKey));
            Assert.That(value.ToString(), Does.Not.Contain(FakePassword));
        }
    }

    [TestCase(null, "us-east-1")]
    [TestCase("", "us-east-1")]
    [TestCase(" ", "us-east-1")]
    [TestCase("dev", "us-east-1")]
    [TestCase("HOM", "us-east-1")]
    [TestCase("hom", null)]
    [TestCase("hom", "")]
    [TestCase("hom", "us-east-2")]
    public void RejectsInvalidEnvironmentBeforeCreatingClients(string? name, string? region)
    {
        var creations = 0;
        var bootstrap = Bootstrap(name, region, _ =>
            {
                creations++;
                return parameters;
            },
            _ =>
            {
                creations++;
                return secrets;
            });
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.InitializeAsync());

        Assert.That(creations, Is.Zero);

        AssertSanitized(error!);
    }

    [TestCase("base/v2/jwt-secret-arn")]
    [TestCase("base/v2/jwt-issuer")]
    [TestCase("base/v2/jwt-audience")]
    [TestCase("database/v2/endpoint")]
    [TestCase("database/v2/port")]
    [TestCase("database/v2/database-name")]
    [TestCase("database/v2/auth-secret-arn")]
    [TestCase("database/v2/auth-db-user")]
    [TestCase("database/v2/ssl-mode")]
    public void RejectsMissingParameter(string field)
    {
        response.Parameters.Remove(Parameter(field));
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    public void RejectsEmptyParameter(string? value)
    {
        Parameter("base/v2/jwt-issuer").Value = value;
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [Test]
    public void RejectsDuplicateParameter()
    {
        response.Parameters.Add(Parameter("database/v2/endpoint"));
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [Test]
    public void RejectsNonStringAndInvalidParameterResponses()
    {
        Parameter("base/v2/jwt-issuer").Type = ParameterType.SecureString;

        Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync());

        Configure("hom");
        response.InvalidParameters = ["/invalid"];

        Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync());

        Configure("hom");
        response.Parameters = null;

        Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync());
    }

    [TestCase("0")]
    [TestCase("65536")]
    [TestCase("-1")]
    [TestCase("5432x")]
    [TestCase(" 5432")]
    public void RejectsInvalidPort(string value)
    {
        Parameter("database/v2/port").Value = value;
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [TestCase("database/v2/auth-db-user", "mecanica_api")]
    [TestCase("database/v2/ssl-mode", "require")]
    public void RejectsIncorrectDatabaseParameters(string field, string value)
    {
        Parameter(field).Value = value;
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [TestCase("base/v2/jwt-secret-arn", "prd", "us-east-1", "121754142617", "base/jwt", "aB1234")]
    [TestCase("base/v2/jwt-secret-arn", "hom", "us-east-2", "121754142617", "base/jwt", "aB1234")]
    [TestCase("base/v2/jwt-secret-arn", "hom", "us-east-1", "123456789012", "base/jwt", "aB1234")]
    [TestCase("database/v2/auth-secret-arn", "hom", "us-east-1", "121754142617", "database/api", "aB1234")]
    [TestCase("database/v2/auth-secret-arn", "hom", "us-east-1", "121754142617", "database/auth", "aB123")]
    [TestCase("database/v2/auth-secret-arn", "hom", "us-east-1", "121754142617", "database/auth", "aB123_")]
    public void RejectsUnexpectedSecretArn(string field, string name, string region, string account, string resource,
        string suffix)
    {
        Parameter(field).Value =
            $"arn:aws:secretsmanager:{region}:{account}:secret:/mecanica/{name}/{resource}-{suffix}";
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);

        Assert.That(secrets.ReceivedCalls(), Is.Empty);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"key\":123}")]
    [TestCase("{\"key\":\" \"}")]
    [TestCase("{\"key\":\"short\"}")]
    public void RejectsInvalidJwtSecret(string? value)
    {
        jwtJson = value;
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [TestCase(null)]
    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("{\"username\":\"mecanica_api\",\"password\":\"fake\"}")]
    [TestCase("{\"username\":\"mecanica_auth\",\"password\":\" \"}")]
    [TestCase("{\"username\":\"mecanica_auth\",\"password\":123}")]
    [TestCase("{\"password\":\"fake\"}")]
    public void RejectsInvalidDatabaseSecret(string? value)
    {
        databaseJson = value;
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [Test]
    public void RejectsMissingCertificate()
    {
        certificate = Path.Combine(AppContext.BaseDirectory, "Certificates", "missing.pem");
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DoesNotExposeAwsErrorPayload(bool secretFailure)
    {
        var payload = $"AWS body {FakePassword} {FakeKey}";

        if (secretFailure)
            secrets.GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<GetSecretValueResponse>(new AmazonSecretsManagerException(payload)));
        else
            parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<GetParametersResponse>(
                    new AmazonSimpleSystemsManagementException(payload)));

        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
    }

    [Test]
    public void CancelsBeforeCallingAws()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(cancellation.Token))!);
        Assert.That(parameters.ReceivedCalls(), Is.Empty);
        Assert.That(secrets.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void CancelsInFlightEvenWhenClientIgnoresCancellation()
    {
        parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<GetParametersResponse>().Task);
        using var cancellation = new CancellationTokenSource();
        var pending = LoadAsync(cancellation.Token);
        cancellation.Cancel();
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);

        AssertSanitized(error!);
    }

    [Test]
    public void EnforcesEightSecondDeadline()
    {
        parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<GetParametersResponse>().Task);
        var elapsed = Stopwatch.StartNew();

        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
        Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(7.5)));
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(12)));
    }

    [Test]
    public async Task BootstrapDisposesClientsAndReturnsReusableUnconnectedContext()
    {
        var bootstrap = Bootstrap();

        Assert.That(parameters.ReceivedCalls(), Is.Empty);

        await using var runtime = await bootstrap.InitializeAsync();
        await using var first = runtime.Database.CreateConnection();
        await using var second = runtime.Database.CreateConnection();

        Assert.Multiple(() =>
        {
            Assert.That(first.State, Is.EqualTo(ConnectionState.Closed));
            Assert.That(second.State, Is.EqualTo(ConnectionState.Closed));
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(runtime.Jwt.Key, Is.EqualTo(FakeKey));
            Assert.That(runtime.ToString(), Does.Not.Contain(FakePassword).And.Not.Contain(FakeKey));
        });

        parameters.Received(1).Dispose();
        secrets.Received(1).Dispose();
        await parameters.Received(2).GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DiscoveryUsesTwoBoundedBatchesAndNoEndpointOverride()
    {
        var configuration = await LoadAsync();
        var requests = parameters.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "GetParametersAsync")
            .Select(call => (GetParametersRequest)call.GetArguments()[0]!).ToArray();
        Assert.That(requests.Select(request => request.Names.Count), Is.EqualTo(new[] { 9, 2 }));
        Assert.That(requests.All(request => request.WithDecryption == false), Is.True);
        Assert.That(configuration.Email!.FunctionArn, Is.EqualTo($"arn:aws:lambda:us-east-1:121754142617:function:mecanica-{environment}-email:live"));
        Assert.That(EmailInvocationConfiguration.ClientConfiguration().IgnoreConfiguredEndpointUrls, Is.True);
    }

    [TestCase("contract-version", "2")]
    [TestCase("invocation-arn", "arn:aws:lambda:us-east-1:121754142617:function:mecanica-prd-email:live")]
    [TestCase("invocation-arn", "http://localhost:3001")]
    public void InvalidEmailDiscoveryFailsBeforeReadingSecrets(string field, string value)
    {
        Parameter("email/v1/" + field).Value = value;
        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync())!);
        Assert.That(secrets.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void BootstrapDisposesClientsOnLoadFailure()
    {
        jwtJson = "{";

        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => Bootstrap().InitializeAsync())!);

        parameters.Received(1).Dispose();
        secrets.Received(1).Dispose();
    }

    [Test]
    public void BootstrapDisposesFirstClientIfSecondFactoryFails()
    {
        var bootstrap = Bootstrap(secretFactory: _ => throw new InvalidOperationException(FakePassword + FakeKey));

        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.InitializeAsync())!);
        parameters.Received(1).Dispose();
    }

    [Test]
    public void BootstrapSanitizesFirstClientFactoryFailure()
    {
        var bootstrap =
            Bootstrap(parameterFactory: _ => throw new AmazonSimpleSystemsManagementException(FakePassword + FakeKey));

        AssertSanitized(Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.InitializeAsync())!);
        Assert.That(secrets.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public async Task DataSourceHonorsCanceledOpenWithoutExternalAccess()
    {
        await using var runtime = new FunctionRuntime(await LoadAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await using var connection = await runtime.Database.OpenConnectionAsync(cancellation.Token);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RuntimeDisposesDataSource(bool asynchronous)
    {
        var runtime = new FunctionRuntime(await LoadAsync());

        if (asynchronous)
            await runtime.DisposeAsync();
        else
            runtime.Dispose();

        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var connection = await runtime.Database.OpenConnectionAsync();
        });
    }
    [Test]
    public async Task InvalidDocumentDoesNotLoadAwsConfiguration()
    {
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(Bootstrap().InitializeAsync));
        var context = Substitute.For<ILambdaContext>();
        context.RemainingTime.Returns(TimeSpan.FromSeconds(15));
        context.Logger.Returns(Substitute.For<ILambdaLogger>());
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/customers/auth/request-code",
            Body = "{\"document\":\"invalid\"}",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.That((await handler.HandleAsync(request, context)).StatusCode, Is.EqualTo(400));

        await parameters.DidNotReceive().GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>());
        await secrets.DidNotReceive().GetSecretValueAsync(Arg.Any<GetSecretValueRequest>(), Arg.Any<CancellationToken>());

    }

    [Test]
    public async Task HandlerDeadlineCancelsAwsInitializationAndDisposesClients()
    {
        CancellationToken receivedCancellation = default;
        parameters.GetParametersAsync(Arg.Any<GetParametersRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                receivedCancellation = call.Arg<CancellationToken>();
                await Task.Delay(Timeout.InfiniteTimeSpan, receivedCancellation);
                return response;
            });
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(Bootstrap().InitializeAsync));
        var context = Substitute.For<ILambdaContext>();
        context.RemainingTime.Returns(TimeSpan.FromSeconds(1.1));
        context.Logger.Returns(Substitute.For<ILambdaLogger>());
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = "/customers/auth/request-code",
            Body = "{\"document\":\"52998224725\"}",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };

        var result = await handler.HandleAsync(request, context);
        await handler.DisposeAsync();

        Assert.That(result.StatusCode, Is.EqualTo(500));
        Assert.That(receivedCancellation.IsCancellationRequested, Is.True);
        parameters.Received(1).Dispose();
        secrets.Received(1).Dispose();
        Assert.That(result.Body, Does.Not.Contain(FakeKey).And.Not.Contain(FakePassword));
    }
}