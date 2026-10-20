using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using NSubstitute;
using System.Text.Json;
using Dapper;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using GerenciamentoMecanica.Autenticacao.Function;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;
using Npgsql;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Testcontainers.PostgreSql;
using PostgreSqlConfiguration = GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration.PostgreSqlConfiguration;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

[NonParallelizable]
public class PostgreSqlCustomerLookupTests
{
    private const string Cpf = "529.982.247-25";
    private const string Cnpj = "10.359.666/0001-94";

    private static readonly Guid CpfId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CnpjId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16.15")
        .WithDatabase("mecanica").WithUsername("postgres").WithPassword("test-only-admin-password").Build();

    private NpgsqlDataSource admin = null!;
    private NpgsqlDataSource authentication = null!;
    private NpgsqlConnectionStringBuilder authenticationSettings = null!;
    private PostgreSqlCustomerLookup lookup = null!;
    private readonly string applicationName = "part3-auth-" + Guid.NewGuid().ToString("N");

    [Test]
    public async Task AbsentCustomerCountsASuccessfulConnectionAndQuery()
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var instrumented = new PostgreSqlCustomerLookup(authentication, metrics);

        var customer = await instrumented.FindByDocumentAsync("00000000000", CancellationToken.None);

        Assert.That(customer, Is.Null);
        Assert.That(listener.Measurements.Select(point => point["operation"]), Is.EqualTo(new[] { "connection.open", "command.execute" }));
        Assert.That(listener.Measurements.All(point => Equals(point["outcome"], "success")), Is.True);
    }

    [Test]
    public async Task InvalidPersistedCustomerDoesNotTurnTheQueryIntoADatabaseFailure()
    {
        await ExecuteAdminAsync("UPDATE customers SET status = 'Unknown' WHERE id = @CpfId", new { CpfId });
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var instrumented = new PostgreSqlCustomerLookup(authentication, metrics);

        Assert.ThrowsAsync<InvalidDataException>(() => instrumented.FindByDocumentAsync(Cpf, CancellationToken.None));

        Assert.That(listener.Measurements.Count, Is.EqualTo(2));
        Assert.That(listener.Measurements.All(point => Equals(point["outcome"], "success")), Is.True);
    }

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        await container.StartAsync();

        var adminSettings = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            SslMode = SslMode.Disable,
            Pooling = false
        };

        admin = NpgsqlDataSource.Create(adminSettings.ConnectionString);

        await ExecuteAdminAsync("""
                                CREATE ROLE mecanica_auth LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
                                    NOREPLICATION NOBYPASSRLS PASSWORD 'test-only-auth-password';
                                GRANT CONNECT ON DATABASE mecanica TO mecanica_auth;
                                REVOKE ALL ON SCHEMA public FROM PUBLIC;
                                GRANT USAGE ON SCHEMA public TO mecanica_auth;
                                """);

        authenticationSettings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString)
        {
            Username = "mecanica_auth",
            Password = "test-only-auth-password",
            ApplicationName = applicationName,
            Timeout = 5,
            CommandTimeout = 5
        };

        authentication = NpgsqlDataSource.Create(authenticationSettings.ConnectionString);
        lookup = new PostgreSqlCustomerLookup(authentication);
    }

    [SetUp]
    public async Task SeedIsolatedFixture()
    {
        await ExecuteAdminAsync("""
                                DROP TABLE IF EXISTS public.customers;
                                CREATE TABLE public.customers (
                                    id uuid PRIMARY KEY,
                                    name varchar(100) NOT NULL,
                                    document varchar(100) NOT NULL UNIQUE,
                                    status varchar(8) NOT NULL DEFAULT 'Active',
                                    phone varchar(100) NOT NULL,
                                    email varchar(100) NOT NULL);
                                GRANT SELECT (id, name, document, status) ON public.customers TO mecanica_auth;
                                """);

        await ExecuteAdminAsync("""
                                INSERT INTO public.customers (id, name, document, phone, email)
                                VALUES (@CpfId, 'Cliente CPF', @Cpf, 'test-phone', 'cpf@example.invalid'),
                                       (@CnpjId, 'Cliente CNPJ', @Cnpj, 'test-phone', 'cnpj@example.invalid');
                                """, new { CpfId, Cpf, CnpjId, Cnpj });
    }

    [OneTimeTearDown]
    public async Task StopDatabase()
    {
        if (authentication is not null)
            await authentication.DisposeAsync();

        if (admin is not null)
            await admin.DisposeAsync();

        await container.DisposeAsync();
    }

    private async Task ExecuteAdminAsync(string sql, object? parameters = null)
    {
        await using var connection = await admin.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    private async Task WaitForActivityAsync(string predicate, bool expected)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            await using var observer = await admin.OpenConnectionAsync();

            var count = await observer.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @Name " + predicate,
                new { Name = applicationName });

            if ((count > 0) == expected)
                return;

            await Task.Delay(25);
        }

        Assert.Fail("Authentication connection activity did not reach the expected state.");
    }

    private Task AssertNoOpenConnectionsAsync() => WaitForActivityAsync("", false);

    [TestCase(Cpf, "Cliente CPF")]
    [TestCase(Cnpj, "Cliente CNPJ")]
    public async Task QueriesOnlyGrantedColumnsAndClosesConnection(string document, string name)
    {
        var customer = await lookup.FindByDocumentAsync(document, CancellationToken.None);

        Assert.That(customer, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(customer!.Document, Is.EqualTo(document));
            Assert.That(customer.Name, Is.EqualTo(name));
            Assert.That(customer.Status, Is.EqualTo(CustomerStatus.Active));
            Assert.That(customer.Id, Is.EqualTo(document == Cpf ? CpfId : CnpjId));
        });

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task AbsentCustomerReturnsNullAndClosesConnection()
    {
        Assert.That(await lookup.FindByDocumentAsync("417.384.220-11", CancellationToken.None), Is.Null);

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task DocumentIsAParameterRatherThanSql()
    {
        Assert.That(await lookup.FindByDocumentAsync("' OR true --", CancellationToken.None), Is.Null);
        Assert.That(await lookup.FindByDocumentAsync(Cpf, CancellationToken.None), Is.Not.Null);

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task InactiveCustomerIsMappedWithoutBeingFilteredOut()
    {
        await ExecuteAdminAsync("UPDATE public.customers SET status = 'Inactive' WHERE document = @Document",
            new { Document = Cpf });

        var customer = await lookup.FindByDocumentAsync(Cpf, CancellationToken.None);

        Assert.That(customer!.Status, Is.EqualTo(CustomerStatus.Inactive));
        Assert.That(customer.IsEligible, Is.False);

        await AssertNoOpenConnectionsAsync();
    }

    [TestCase("Deleted")]
    [TestCase("active")]
    [TestCase("")]
    public async Task UnknownStatusIsTechnicalAndConnectionCloses(string status)
    {
        await ExecuteAdminAsync("UPDATE public.customers SET status = @Status WHERE document = @Document",
            new { Status = status, Document = Cpf });

        Assert.ThrowsAsync<InvalidDataException>(() => lookup.FindByDocumentAsync(Cpf, CancellationToken.None));

        await AssertNoOpenConnectionsAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvalidStoredIdentityIsTechnicalAndConnectionCloses(bool emptyId)
    {
        await ExecuteAdminAsync(emptyId
                ? "UPDATE public.customers SET id = @Id WHERE document = @Document"
                : "UPDATE public.customers SET name = ' ' WHERE document = @Document",
            new { Id = Guid.Empty, Document = Cpf });

        Assert.ThrowsAsync<InvalidDataException>(() => lookup.FindByDocumentAsync(Cpf, CancellationToken.None));

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task MultipleMatchesAreTechnicalAndConnectionCloses()
    {
        await ExecuteAdminAsync("""
                                ALTER TABLE public.customers DROP CONSTRAINT customers_document_key;
                                INSERT INTO public.customers (id, name, document, phone, email)
                                VALUES (@Id, 'Duplicado', @Document, 'test-phone', 'duplicate@example.invalid');
                                """, new { Id = Guid.NewGuid(), Document = Cpf });

        Assert.ThrowsAsync<InvalidOperationException>(() => lookup.FindByDocumentAsync(Cpf, CancellationToken.None));

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task SqlFailureIsTechnicalAndConnectionCloses()
    {
        await ExecuteAdminAsync("ALTER TABLE public.customers DROP COLUMN status");

        Assert.ThrowsAsync<PostgresException>(() => lookup.FindByDocumentAsync(Cpf, CancellationToken.None));

        await AssertNoOpenConnectionsAsync();
    }

    [TestCase("SELECT phone FROM public.customers")]
    [TestCase("SELECT email FROM public.customers")]
    [TestCase("SELECT * FROM public.customers")]
    [TestCase("UPDATE public.customers SET name = 'forbidden'")]
    [TestCase("DELETE FROM public.customers")]
    [TestCase(
        "INSERT INTO public.customers (id, name, document, phone, email) VALUES (gen_random_uuid(), 'forbidden', 'forbidden', '', '')")]
    public async Task AuthRoleCannotReadOtherColumnsOrWrite(string sql)
    {
        await using (var connection = await authentication.OpenConnectionAsync())
        {
            var error = Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql));
            Assert.That(error!.SqlState, Is.EqualTo(PostgresErrorCodes.InsufficientPrivilege));
        }

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task PreCanceledLookupDoesNotLeaveConnection()
    {
        Assert.CatchAsync<OperationCanceledException>(() =>
            lookup.FindByDocumentAsync(Cpf, new CancellationToken(true)));

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task InFlightCancellationClosesBlockedConnection()
    {
        await using var blocker = await admin.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();

        await blocker.ExecuteAsync("LOCK TABLE public.customers IN ACCESS EXCLUSIVE MODE", transaction: transaction);

        using var cancellation = new CancellationTokenSource();
        var pending = lookup.FindByDocumentAsync(Cpf, cancellation.Token);

        try
        {
            await WaitForActivityAsync("AND wait_event_type = 'Lock'", true);
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.CatchAsync<OperationCanceledException>(async () => await pending);

        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task FiveSecondCommandTimeoutClosesBlockedConnection()
    {
        await using var blocker = await admin.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();

        await blocker.ExecuteAsync("LOCK TABLE public.customers IN ACCESS EXCLUSIVE MODE", transaction: transaction);

        var elapsed = Stopwatch.StartNew();
        var error = Assert.ThrowsAsync<NpgsqlException>(() => lookup.FindByDocumentAsync(Cpf, CancellationToken.None));

        Assert.That(error!.InnerException, Is.TypeOf<TimeoutException>());
        Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5)));
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(12)));

        await AssertNoOpenConnectionsAsync();
    }

    [TestCase("52998224725", Cpf)]
    [TestCase(Cpf, Cpf)]
    [TestCase("10359666000194", Cnpj)]
    [TestCase(Cnpj, Cnpj)]
    public async Task ComposedRuntimeAuthenticatesStoredCustomer(string input, string canonical)
    {
        var settings = new RuntimeConfiguration(
            new JwtConfiguration("test-only-jwt-key-with-at-least-32-characters", "test-issuer", "test-audience"),
            new PostgreSqlConfiguration(authenticationSettings));

        await using var runtime = new FunctionRuntime(settings);
        var result = await runtime.Authentication.ExecuteAsync(input);

        Assert.That(result.Outcome, Is.EqualTo(CustomerAuthenticationOutcome.Authenticated));

        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Token!);

        Assert.That(token.Claims.Any(claim => claim.Value == (canonical == Cpf ? CpfId : CnpjId).ToString()), Is.True);
        Assert.That(token.Claims.Any(claim => claim.Value == "Customer"), Is.True);

        await AssertNoOpenConnectionsAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ComposedRuntimeDoesNotAuthenticateMissingOrInactiveCustomer(bool inactive)
    {
        await ExecuteAdminAsync(inactive
            ? "UPDATE public.customers SET status = 'Inactive' WHERE document = @Document"
            : "DELETE FROM public.customers WHERE document = @Document", new { Document = Cpf });

        var settings = new RuntimeConfiguration(
            new JwtConfiguration("test-only-jwt-key-with-at-least-32-characters", "test-issuer", "test-audience"),
            new PostgreSqlConfiguration(authenticationSettings));

        await using var runtime = new FunctionRuntime(settings);
        var result = await runtime.Authentication.ExecuteAsync(Cpf);

        Assert.That(result.Outcome, Is.EqualTo(CustomerAuthenticationOutcome.NotEligible));
        Assert.That(result.Token, Is.Null);

        await AssertNoOpenConnectionsAsync();
    }
    private FunctionRuntime HttpRuntime() => new(new RuntimeConfiguration(
        new JwtConfiguration("test-only-jwt-key-with-at-least-32-characters", "test-issuer", "test-audience"),
        new PostgreSqlConfiguration(authenticationSettings)));

    private static APIGatewayProxyRequest HttpRequest(string document) => new()
    {
        HttpMethod = "POST",
        Path = "/customers/validate",
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        Body = JsonSerializer.Serialize(new { document })
    };

    private static ILambdaContext HttpContext(double seconds = 15)
    {
        var context = Substitute.For<ILambdaContext>();
        context.RemainingTime.Returns(TimeSpan.FromSeconds(seconds));
        context.Logger.Returns(Substitute.For<ILambdaLogger>());
        context.AwsRequestId.Returns("test-request");
        return context;
    }

    [TestCase("52998224725", Cpf)]
    [TestCase("10359666000194", Cnpj)]
    public async Task HttpHandlerQueriesPostgreSqlAndReturnsRealToken(string input, string canonical)
    {
        var initializations = 0;
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ =>
        {
            initializations++;
            return Task.FromResult(HttpRuntime());
        }));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await handler.HandleAsync(HttpRequest(input), HttpContext());
            Assert.That(response.StatusCode, Is.EqualTo(200));
            var encoded = JsonSerializer.Deserialize<string>(response.Body)!;
            var token = new JwtSecurityTokenHandler().ReadJwtToken(encoded);
            Assert.That(token.Claims.Any(claim => claim.Value == (canonical == Cpf ? CpfId : CnpjId).ToString()), Is.True);
            Assert.That(token.Claims.Any(claim => claim.Value == "Customer"), Is.True);
            Assert.That(token.Issuer, Is.EqualTo("test-issuer"));
            await AssertNoOpenConnectionsAsync();
        }

        Assert.That(initializations, Is.EqualTo(1));
    }

    [Test]
    public async Task HttpHandlerMissingAndInactiveCustomerReturnSameProblem()
    {
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime())));
        await ExecuteAdminAsync("UPDATE public.customers SET status = 'Inactive' WHERE document = @Document", new { Document = Cpf });
        var inactive = await handler.HandleAsync(HttpRequest(Cpf), HttpContext());
        await ExecuteAdminAsync("DELETE FROM public.customers WHERE document = @Document", new { Document = Cpf });
        var missing = await handler.HandleAsync(HttpRequest(Cpf), HttpContext());
        Assert.That(inactive.StatusCode, Is.EqualTo(401));
        Assert.That(missing.StatusCode, Is.EqualTo(401));
        Assert.That(missing.Body, Is.EqualTo(inactive.Body));
        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task HttpHandlerSqlFailureReturns500AndClosesConnection()
    {
        await ExecuteAdminAsync("ALTER TABLE public.customers DROP COLUMN status");
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime())));
        var response = await handler.HandleAsync(HttpRequest(Cpf), HttpContext());
        Assert.That(response.StatusCode, Is.EqualTo(500));
        Assert.That(response.Body, Does.Not.Contain("Postgres").And.Not.Contain("column"));
        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task HttpDeadlineCancelsBlockedSqlAndClosesConnection()
    {
        await using var blocker = await admin.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("LOCK TABLE public.customers IN ACCESS EXCLUSIVE MODE", transaction: transaction);
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(HttpRuntime())));
        var response = await handler.HandleAsync(HttpRequest(Cpf), HttpContext(1.2));
        Assert.That(response.StatusCode, Is.EqualTo(500));
        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task InvalidHttpBodyDoesNotInitializeRuntime()
    {
        var initializations = 0;
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ =>
        {
            initializations++;
            return Task.FromResult(HttpRuntime());
        }));
        var request = HttpRequest(Cpf);
        request.Body = "{}";
        Assert.That((await handler.HandleAsync(request, HttpContext())).StatusCode, Is.EqualTo(400));
        Assert.That(initializations, Is.Zero);
        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task FailedInitializationCanRetryAndSuccessfulRuntimeIsKept()
    {
        var initializations = 0;
        await using var handler = new FunctionHandler(new FunctionRuntimeProvider(_ =>
        {
            initializations++;
            return initializations == 1
                ? Task.FromException<FunctionRuntime>(new InvalidOperationException("test-only-sensitive-failure"))
                : Task.FromResult(HttpRuntime());
        }));
        var failure = await handler.HandleAsync(HttpRequest(Cpf), HttpContext());
        Assert.That(failure.StatusCode, Is.EqualTo(500));
        Assert.That(failure.Body, Does.Not.Contain("test-only-sensitive-failure"));
        Assert.That((await handler.HandleAsync(HttpRequest(Cpf), HttpContext())).StatusCode, Is.EqualTo(200));
        Assert.That((await handler.HandleAsync(HttpRequest(Cpf), HttpContext())).StatusCode, Is.EqualTo(200));
        Assert.That(initializations, Is.EqualTo(2));
        await AssertNoOpenConnectionsAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitHandlerDisposalDisposesCachedDataSource(bool asynchronous)
    {
        var runtime = HttpRuntime();
        var handler = new FunctionHandler(new FunctionRuntimeProvider(_ => Task.FromResult(runtime)));
        try
        {
            Assert.That((await handler.HandleAsync(HttpRequest(Cpf), HttpContext())).StatusCode, Is.EqualTo(200));
            if (asynchronous)
                await handler.DisposeAsync();
            else
                handler.Dispose();

            Assert.ThrowsAsync<ObjectDisposedException>(async () => await runtime.Database.OpenConnectionAsync());
            Assert.That((await handler.HandleAsync(HttpRequest(Cpf), HttpContext())).StatusCode, Is.EqualTo(500));
            await handler.DisposeAsync();
            handler.Dispose();
        }
        finally
        {
            await handler.DisposeAsync();
        }
        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task CanceledInitializationDoesNotCacheRuntimeAndDisposesIt()
    {
        var abandoned = HttpRuntime();
        using var cancellation = new CancellationTokenSource();
        var initializations = 0;
        await using var provider = new FunctionRuntimeProvider(_ =>
        {
            initializations++;
            if (initializations != 1)
                return Task.FromResult(HttpRuntime());
            cancellation.Cancel();
            return Task.FromResult(abandoned);
        });

        Assert.CatchAsync<OperationCanceledException>(() => provider.ExecuteAsync(Cpf, cancellation.Token));
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await abandoned.Database.OpenConnectionAsync());
        Assert.That((await provider.ExecuteAsync(Cpf, CancellationToken.None)).Outcome,
            Is.EqualTo(CustomerAuthenticationOutcome.Authenticated));
        Assert.That(initializations, Is.EqualTo(2));
        await AssertNoOpenConnectionsAsync();
    }

    [Test]
    public async Task ConcurrentCallsInitializeOnlyOnce()
    {
        var initializations = 0;
        await using var provider = new FunctionRuntimeProvider(async cancellation =>
        {
            Interlocked.Increment(ref initializations);
            await Task.Delay(25, cancellation);
            return HttpRuntime();
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => provider.ExecuteAsync(Cpf, CancellationToken.None)));
        Assert.That(results.All(result => result.Outcome == CustomerAuthenticationOutcome.Authenticated), Is.True);
        Assert.That(initializations, Is.EqualTo(1));
        await AssertNoOpenConnectionsAsync();
    }
}
