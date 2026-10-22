using System.Security.Cryptography;
using System.Diagnostics;
using Dapper;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

[NonParallelizable]
public sealed class PostgreSqlCustomerAccessStoreTests
{
    private const string Document = "529.982.247-25";
    private const string OtherDocument = "10.359.666/0001-94";
    private const string Code = "01234567";
    private static readonly Guid CustomerId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid OtherCustomerId = Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa");
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16.15")
        .WithDatabase("access_tests").WithUsername("postgres").WithPassword("local-tests-only-password").Build();
    private NpgsqlDataSource admin = null!;
    private NpgsqlDataSource authentication = null!;
    private PostgreSqlCustomerAccessStore store = null!;
    private ILogger<PostgreSqlCustomerAccessStore> logger = null!;
    private ICustomerTokenIssuer tokens = null!;
    private int generatedCodes;

    [OneTimeSetUp]
    public async Task StartDatabase()
    {
        await container.StartAsync();
        var settings = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Pooling = false, SslMode = SslMode.Disable };
        admin = NpgsqlDataSource.Create(settings.ConnectionString);
        await SqlAsync("""
            CREATE ROLE access_auth LOGIN NOINHERIT PASSWORD 'local-tests-only-auth';
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            GRANT USAGE ON SCHEMA public TO access_auth;
            CREATE TABLE customers (id UUID PRIMARY KEY, name VARCHAR(100) NOT NULL, document VARCHAR(100) NOT NULL UNIQUE,
                status VARCHAR(8) NOT NULL DEFAULT 'Active', email VARCHAR(100) NOT NULL);
            CREATE TABLE customer_auth_challenges (id UUID PRIMARY KEY, customer_id UUID NOT NULL, code_salt BYTEA NOT NULL,
                code_verifier BYTEA NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                expires_at TIMESTAMPTZ NOT NULL, state VARCHAR(50) NOT NULL DEFAULT 'Pending',
                failed_attempts INTEGER NOT NULL DEFAULT 0, consumed_at TIMESTAMPTZ, invalidated_at TIMESTAMPTZ);
            CREATE TABLE customer_auth_request_limits (customer_id UUID PRIMARY KEY, window_started_at TIMESTAMPTZ NOT NULL,
                window_expires_at TIMESTAMPTZ NOT NULL, request_count INTEGER NOT NULL, last_requested_at TIMESTAMPTZ NOT NULL);
            GRANT SELECT (id, name, document, status, email) ON customers TO access_auth;
            """);
        settings.Username = "access_auth";
        settings.Password = "local-tests-only-auth";
        settings.ApplicationName = "ae3-access-tests";
        settings.Timeout = 5;
        settings.CommandTimeout = 5;
        authentication = NpgsqlDataSource.Create(settings.ConnectionString);
    }

    [SetUp]
    public async Task ResetData()
    {
        await SqlAsync("""
            TRUNCATE customer_auth_challenges, customer_auth_request_limits, customers;
            GRANT SELECT, INSERT, UPDATE, DELETE ON customer_auth_challenges, customer_auth_request_limits TO access_auth;
            INSERT INTO customers(id,name,document,email) VALUES
                (@CustomerId,'Cliente local',@Document,'customer@example.invalid'),
                (@OtherCustomerId,'Outro cliente local',@OtherDocument,'other@example.invalid');
            """, new { CustomerId, Document, OtherCustomerId, OtherDocument });
        generatedCodes = 0;
        logger = Substitute.For<ILogger<PostgreSqlCustomerAccessStore>>();
        tokens = Substitute.For<ICustomerTokenIssuer>();
        tokens.Generate(Arg.Any<CustomerIdentity>()).Returns("local-token");
        store = NewStore();
    }

    private PostgreSqlCustomerAccessStore NewStore() => new(authentication, logger,
        generateCode: () => { Interlocked.Increment(ref generatedCodes); return Code; });

    [TearDown]
    public async Task EveryOperationClosesItsAuthenticationConnection()
    {
        Assert.That(await ScalarAsync<int>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name='ae3-access-tests'"), Is.Zero);
    }

    [OneTimeTearDown]
    public async Task StopDatabase()
    {
        if (authentication is not null) await authentication.DisposeAsync();
        if (admin is not null) await admin.DisposeAsync();
        await container.DisposeAsync();
    }

    [Test]
    public async Task MissingCustomerReturnsBeforeCodeGenerationOrAnyAccessToTheAuthTables()
    {
        await SqlAsync("DELETE FROM customers WHERE id=@CustomerId", new { CustomerId });
        await SqlAsync("REVOKE ALL ON customer_auth_challenges, customer_auth_request_limits FROM access_auth");

        var result = await store.CreatePendingAsync(Document);

        Assert.That(result.Outcome, Is.EqualTo(CustomerCodeRequestOutcome.CustomerNotFound));
        Assert.That(result.ChallengeId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(generatedCodes, Is.Zero);
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_request_limits"), Is.Zero);
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_challenges"), Is.Zero);
        var log = logger.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "Log").GetArguments();
        Assert.That(log[0], Is.EqualTo(LogLevel.Information));
        Assert.That(log[2]!.ToString(), Is.EqualTo("Solicitação de autenticação para cliente não encontrado."));
        Assert.That(log[3], Is.Null);
    }

    [TestCase("Inactive", "customer@example.invalid")]
    [TestCase("Active", "")]
    [TestCase("Active", "invalid email")]
    public async Task IneligibleCustomerDoesNotWriteOrGenerateCode(string status, string email)
    {
        await SqlAsync("UPDATE customers SET status=@status,email=@email WHERE id=@CustomerId", new { status, email, CustomerId });
        await SqlAsync("REVOKE ALL ON customer_auth_challenges, customer_auth_request_limits FROM access_auth");

        var result = await store.CreatePendingAsync(Document);

        Assert.That(result.Outcome, Is.EqualTo(CustomerCodeRequestOutcome.NotEligible));
        Assert.That(generatedCodes, Is.Zero);
    }

    [Test]
    public async Task CreationPersistsPendingAndOnlyTheSaltAndVerifier()
    {
        var result = await store.CreatePendingAsync(Document);

        var row = await ChallengeAsync(result.ChallengeId);
        Assert.That(result.Outcome, Is.EqualTo(CustomerCodeRequestOutcome.Pending));
        Assert.That(result.Code, Is.EqualTo(Code)); // Leading zero must survive.
        Assert.That(row.State, Is.EqualTo("Pending"));
        Assert.That(row.Salt, Has.Length.EqualTo(16));
        Assert.That(row.Verifier, Has.Length.EqualTo(32));
        var expected = Rfc2898DeriveBytes.Pbkdf2(Code, row.Salt, 600_000, HashAlgorithmName.SHA256, 32);
        Assert.That(CryptographicOperations.FixedTimeEquals(row.Verifier, expected), Is.True);
        Assert.That(row.ExpiresAt-row.CreatedAt, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(result.ToString(), Does.Not.Contain(Code).And.Not.Contain("customer@example.invalid"));
    }

    [Test]
    public async Task PendingCannotAuthenticateBeforeCaptureIsConfirmed()
    {
        var pending = await store.CreatePendingAsync(Document);

        var result = await store.VerifyAndConsumeAsync(pending.ChallengeId, Document, Code, tokens);

        Assert.That(result.Outcome, Is.EqualTo(CustomerAuthenticationOutcome.NotEligible));
        Assert.That((await ChallengeAsync(pending.ChallengeId)).FailedAttempts, Is.Zero);
        tokens.DidNotReceive().Generate(Arg.Any<CustomerIdentity>());
    }

    [Test]
    public async Task SuccessfulVerificationReturnsTheTokenAfterConsumptionIsCommitted()
    {
        var ready = await ReadyAsync();

        var result = await store.VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens);

        Assert.That(result.Token, Is.EqualTo("local-token"));
        var stored = await ChallengeAsync(ready.ChallengeId);
        Assert.That(stored.State, Is.EqualTo("Consumed"));
        Assert.That(stored.ConsumedAt, Is.Not.Null);
        tokens.Received(1).Generate(Arg.Is<CustomerIdentity>(customer => customer.Id == CustomerId));
        Assert.That((await store.VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens)).Token, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WrongCodeOrDocumentCommitsEachAttemptAndTheFifthInvalidates(bool wrongDocument)
    {
        var ready = await ReadyAsync();
        for (var attempt=1; attempt<=5; attempt++)
        {
            var result = await store.VerifyAndConsumeAsync(ready.ChallengeId,
                wrongDocument ? OtherDocument : Document, wrongDocument ? Code : "87654321", tokens);

            Assert.That(result.Token, Is.Null);
            var stored = await ChallengeAsync(ready.ChallengeId);
            Assert.That(stored.FailedAttempts, Is.EqualTo(attempt));
            Assert.That(stored.State, Is.EqualTo(attempt == 5 ? "Invalidated" : "Ready"));
        }
        Assert.That((await store.VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens)).Token, Is.Null);
        Assert.That((await ChallengeAsync(ready.ChallengeId)).FailedAttempts, Is.EqualTo(5));
        tokens.DidNotReceive().Generate(Arg.Any<CustomerIdentity>());
    }

    [Test]
    public async Task CooldownDoesNotChangeTheCountTimestampOrExistingChallenge()
    {
        var first = await store.CreatePendingAsync(Document);
        var before = await LimitAsync();

        var blocked = await store.CreatePendingAsync(Document);

        Assert.That(blocked.Outcome, Is.EqualTo(CustomerCodeRequestOutcome.RateLimited));
        Assert.That(blocked.RetryAfterSeconds, Is.InRange(1,60));
        var after = await LimitAsync();
        Assert.That(after.RequestCount, Is.EqualTo(1));
        Assert.That(after.LastRequestedAt, Is.EqualTo(before.LastRequestedAt));
        Assert.That((await ChallengeAsync(first.ChallengeId)).State, Is.EqualTo("Pending"));
        Assert.That(generatedCodes, Is.EqualTo(1));
    }

    [Test]
    public async Task FiveAdmittedRequestsBlockTheSixthWithinTheSameHour()
    {
        await store.CreatePendingAsync(Document);
        for (var count=2; count<=5; count++)
        {
            await PermitNextRequestAsync();
            Assert.That((await store.CreatePendingAsync(Document)).Outcome, Is.EqualTo(CustomerCodeRequestOutcome.Pending));
        }
        await PermitNextRequestAsync();

        var sixth = await store.CreatePendingAsync(Document);

        Assert.That(sixth.Outcome, Is.EqualTo(CustomerCodeRequestOutcome.RateLimited));
        Assert.That(sixth.RetryAfterSeconds, Is.GreaterThan(60));
        Assert.That((await LimitAsync()).RequestCount, Is.EqualTo(5));
        Assert.That(generatedCodes, Is.EqualTo(5));
    }

    [Test]
    public async Task ReplacingAChallengePreventsAnOldCaptureFromReactivatingIt()
    {
        var first = await ReadyAsync();
        await PermitNextRequestAsync();

        var replacement = await store.CreatePendingAsync(Document);

        Assert.That((await ChallengeAsync(first.ChallengeId)).State, Is.EqualTo("Invalidated"));
        Assert.That(await store.ActivateAsync(CustomerId, first.ChallengeId), Is.False);
        Assert.That((await ChallengeAsync(replacement.ChallengeId)).State, Is.EqualTo("Pending"));
        Assert.That(await store.ActivateAsync(CustomerId, replacement.ChallengeId), Is.True);
    }

    [Test]
    public async Task FailedDeliveryKeepsTheAdmittedRequestCountAndDoesNotRestoreOldCodes()
    {
        var first = await ReadyAsync();
        await PermitNextRequestAsync();
        var pending = await store.CreatePendingAsync(Document);

        Assert.That(await store.InvalidateAsync(CustomerId, pending.ChallengeId), Is.True);

        Assert.That((await LimitAsync()).RequestCount, Is.EqualTo(2));
        Assert.That((await ChallengeAsync(first.ChallengeId)).State, Is.EqualTo("Invalidated"));
        Assert.That((await ChallengeAsync(pending.ChallengeId)).State, Is.EqualTo("Invalidated"));
    }

    [Test]
    public async Task ConcurrentRequestsAcrossStoresCreateOnlyOneChallenge()
    {
        var results = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => NewStore().CreatePendingAsync(Document)));

        Assert.That(results.Count(result => result.Outcome == CustomerCodeRequestOutcome.Pending), Is.EqualTo(1));
        Assert.That(results.Count(result => result.Outcome == CustomerCodeRequestOutcome.RateLimited), Is.EqualTo(3));
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_challenges"), Is.EqualTo(1));
        Assert.That((await LimitAsync()).RequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentConfirmationsAcrossStoresReturnOnlyOneToken()
    {
        var ready = await ReadyAsync();

        var results = await Task.WhenAll(Enumerable.Range(0,3).Select(_ =>
            NewStore().VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens)));

        Assert.That(results.Count(result => result.Token is not null), Is.EqualTo(1));
        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Consumed"));
        tokens.Received(1).Generate(Arg.Any<CustomerIdentity>());
    }

    [Test]
    public async Task SigningFailureRollsBackAndLeavesTheCodeAvailableForRetry()
    {
        var ready = await ReadyAsync();
        var original = new InvalidOperationException("local-sensitive-signing-failure");
        tokens.Generate(Arg.Any<CustomerIdentity>()).Returns(_ => throw original);

        var error = Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens));

        Assert.That(error, Is.SameAs(original));
        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Ready"));
        tokens.Generate(Arg.Any<CustomerIdentity>()).Returns("retry-token");
        Assert.That((await store.VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens)).Token, Is.EqualTo("retry-token"));
    }

    [Test]
    public async Task InsertFailureRollsBackTheNewLimitCountAndPreviousInvalidation()
    {
        var ready = await ReadyAsync();
        await PermitNextRequestAsync();
        await SqlAsync("REVOKE INSERT ON customer_auth_challenges FROM access_auth");

        var error = Assert.ThrowsAsync<PostgresException>(() => store.CreatePendingAsync(Document));

        Assert.That(error!.SqlState, Is.EqualTo(PostgresErrorCodes.InsufficientPrivilege));
        Assert.That((await LimitAsync()).RequestCount, Is.EqualTo(1));
        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Ready"));
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_challenges"), Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationBeforeCommitDoesNotConsumeTheCode()
    {
        var ready = await ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        tokens.Generate(Arg.Any<CustomerIdentity>()).Returns(_ => { cancellation.Cancel(); return "not-returned"; });

        Assert.CatchAsync<OperationCanceledException>(() =>
            store.VerifyAndConsumeAsync(ready.ChallengeId, Document, Code, tokens, cancellation.Token));

        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Ready"));
    }

    [Test]
    public async Task CancellationWhileWaitingForTheCustomerLockDoesNotWrite()
    {
        await using var blocker = await admin.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(CAST(@CustomerId AS text),0))",
            new { CustomerId }, transaction);
        using var cancellation = new CancellationTokenSource();
        var request = store.CreatePendingAsync(Document,cancellation.Token);
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (await ScalarAsync<int>("SELECT count(*) FROM pg_stat_activity WHERE application_name='ae3-access-tests' AND wait_event_type='Lock'") == 0)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                    Assert.Fail("Request did not reach the customer lock.");
                await Task.Delay(25);
            }
        }
        finally { cancellation.Cancel(); }

        Assert.CatchAsync<OperationCanceledException>(async () => await request);
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_request_limits"), Is.Zero);
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_challenges"), Is.Zero);
        Assert.That(generatedCodes, Is.Zero);
    }

    [Test]
    public async Task BrokenConnectionDoesNotReplaceTheOriginalFailureWithARollbackError()
    {
        var ready = await ReadyAsync();
        tokens.Generate(Arg.Any<CustomerIdentity>()).Returns(_ =>
        {
            using var observer = admin.OpenConnection();
            var pid = observer.QuerySingle<int>(
                "SELECT pid FROM pg_stat_activity WHERE application_name='ae3-access-tests'");
            observer.Execute("SELECT pg_terminate_backend(@pid)",new { pid });
            return "not-returned";
        });

        Assert.CatchAsync<NpgsqlException>(() => store.VerifyAndConsumeAsync(ready.ChallengeId,Document,Code,tokens));

        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Ready"));
        foreach (var call in logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log"))
        {
            var arguments = call.GetArguments();
            Assert.That(arguments[3], Is.Null); // Never pass an Exception to the logger.
            Assert.That(arguments[2]!.ToString(), Does.Not.Contain(Document).And.Not.Contain(Code));
        }
    }

    [TestCase("1234567")]
    [TestCase("１２３４５６７８")]
    [TestCase("abcdefgh")]
    public void MalformedCodeIsRejectedBeforeOpeningAConnection(string code)
    {
        Assert.ThrowsAsync<ArgumentException>(() => store.VerifyAndConsumeAsync(Guid.NewGuid(),Document,code,tokens));
    }

    [Test]
    public async Task ExpiredChallengeIsNotActivatedOrVerified()
    {
        var pending = await store.CreatePendingAsync(Document);
        var now = await ScalarAsync<DateTime>("SELECT clock_timestamp()");
        await SqlAsync("UPDATE customer_auth_challenges SET created_at=@CreatedAt,expires_at=@ExpiresAt WHERE id=@Id",
            new { Id=pending.ChallengeId, CreatedAt=now.AddMinutes(-6), ExpiresAt=now.AddMinutes(-1) });

        Assert.That(await store.ActivateAsync(CustomerId,pending.ChallengeId), Is.False);
        Assert.That((await store.VerifyAndConsumeAsync(pending.ChallengeId,Document,Code,tokens)).Token, Is.Null);
        Assert.That((await ChallengeAsync(pending.ChallengeId)).FailedAttempts, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CustomerRemovedOrInactivatedAfterCaptureCannotAuthenticate(bool delete)
    {
        var ready = await ReadyAsync();
        await SqlAsync(delete ? "DELETE FROM customers WHERE id=@CustomerId" : "UPDATE customers SET status='Inactive' WHERE id=@CustomerId", new { CustomerId });

        var result = await store.VerifyAndConsumeAsync(ready.ChallengeId,Document,Code,tokens);

        Assert.That(result.Token, Is.Null);
        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Invalidated"));
    }

    [Test]
    public async Task CorruptStoredStateIsRejectedByCsharpWithoutChangingAttempts()
    {
        var ready = await ReadyAsync();
        await SqlAsync("UPDATE customer_auth_challenges SET state='OutsideDomain' WHERE id=@Id",new { Id=ready.ChallengeId });

        Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyAndConsumeAsync(ready.ChallengeId,Document,Code,tokens));

        Assert.That((await ChallengeAsync(ready.ChallengeId)).FailedAttempts, Is.Zero);
        tokens.DidNotReceive().Generate(Arg.Any<CustomerIdentity>());
    }

    [Test]
    public async Task EmptyStoredCustomerIdIsATechnicalDataFailure()
    {
        var ready = await ReadyAsync();
        await SqlAsync("UPDATE customer_auth_challenges SET customer_id=@Empty WHERE id=@Id",
            new { Empty=Guid.Empty, Id=ready.ChallengeId });

        Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyAndConsumeAsync(ready.ChallengeId,Document,Code,tokens));

        Assert.That((await ChallengeAsync(ready.ChallengeId)).State, Is.EqualTo("Ready"));
        tokens.DidNotReceive().Generate(Arg.Any<CustomerIdentity>());
    }

    [Test]
    public async Task LimitsAreIndependentForDifferentCustomers()
    {
        await store.CreatePendingAsync(Document);
        Assert.That((await store.CreatePendingAsync(Document)).Outcome, Is.EqualTo(CustomerCodeRequestOutcome.RateLimited));

        Assert.That((await store.CreatePendingAsync(OtherDocument)).Outcome, Is.EqualTo(CustomerCodeRequestOutcome.Pending));
        Assert.That(await ScalarAsync<int>("SELECT count(*) FROM customer_auth_request_limits"), Is.EqualTo(2));
    }

    private async Task<CustomerCodeRequestResult> ReadyAsync()
    {
        var pending = await store.CreatePendingAsync(Document);
        Assert.That(await store.ActivateAsync(CustomerId,pending.ChallengeId), Is.True);
        return pending;
    }

    // Move only the local persisted window so tests do not sleep for a minute.
    private async Task PermitNextRequestAsync()
    {
        var now = await ScalarAsync<DateTime>("SELECT clock_timestamp()");
        await SqlAsync("""
            UPDATE customer_auth_request_limits SET window_started_at=@Start,window_expires_at=@End,
                last_requested_at=@Last WHERE customer_id=@CustomerId
            """,new { Start=now.AddMinutes(-10), End=now.AddMinutes(50), Last=now.AddSeconds(-61), CustomerId });
    }

    private async Task<AccessChallengeRow> ChallengeAsync(Guid id)
    {
        await using var connection = await admin.OpenConnectionAsync();
        return await connection.QuerySingleAsync<AccessChallengeRow>("""
            SELECT id,customer_id AS CustomerId,code_salt AS Salt,code_verifier AS Verifier,
                created_at AS CreatedAt,expires_at AS ExpiresAt,state,failed_attempts AS FailedAttempts,
                consumed_at AS ConsumedAt,invalidated_at AS InvalidatedAt
            FROM customer_auth_challenges WHERE id=@id
            """,new { id });
    }

    private async Task<AccessRequestLimitRow> LimitAsync()
    {
        await using var connection = await admin.OpenConnectionAsync();
        return await connection.QuerySingleAsync<AccessRequestLimitRow>("""
            SELECT window_started_at AS WindowStartedAt,window_expires_at AS WindowExpiresAt,
                request_count AS RequestCount,last_requested_at AS LastRequestedAt
            FROM customer_auth_request_limits WHERE customer_id=@CustomerId
            """,new { CustomerId });
    }

    private async Task SqlAsync(string sql,object? parameters=null)
    {
        await using var connection = await admin.OpenConnectionAsync();
        await connection.ExecuteAsync(sql,parameters);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await admin.OpenConnectionAsync();
        return await connection.QuerySingleAsync<T>(sql);
    }
}
