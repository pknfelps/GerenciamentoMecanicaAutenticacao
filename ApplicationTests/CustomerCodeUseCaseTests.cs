using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using NSubstitute;

namespace GerenciamentoMecanica.Autenticacao.ApplicationTests;

public class CustomerCodeUseCaseTests
{
    private const string Document = "529.982.247-25";
    private ICustomerAccessStore store = null!;
    private ICustomerCodeSender sender = null!;
    private ICustomerDocumentNormalizer normalizer = null!;
    private ICustomerTokenIssuer tokens = null!;
    private CustomerCodeRequestResult pending = null!;
    private RequestCustomerCodeUseCase RequestCode => new(normalizer, store, sender);
    private VerifyCustomerCodeUseCase VerifyCode => new(normalizer, store, tokens);

    [SetUp]
    public void Setup()
    {
        store = Substitute.For<ICustomerAccessStore>();
        sender = Substitute.For<ICustomerCodeSender>();
        normalizer = Substitute.For<ICustomerDocumentNormalizer>();
        tokens = Substitute.For<ICustomerTokenIssuer>();
        normalizer.Normalize(Document).Returns(Document);
        normalizer.Normalize("invalid").Returns((string?)null);
        pending = CustomerCodeRequestResult.Pending(Guid.NewGuid(),
            new CustomerIdentity(Guid.NewGuid(), "Cliente local", Document, CustomerStatus.Active), "customer@example.invalid", "01234567");
        store.CreatePendingAsync(Document, Arg.Any<CancellationToken>()).Returns(pending);
    }

    [Test]
    public async Task InvalidDocumentNeverTouchesStoreOrSender()
    {
        Assert.That((await RequestCode.ExecuteAsync("invalid")).InvalidDocument, Is.True);
        Assert.That(store.ReceivedCalls(), Is.Empty);
        Assert.That(sender.ReceivedCalls(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrIneligibleCustomerGetsOpaqueIdWithoutSending(bool missing)
    {
        var result = missing ? CustomerCodeRequestResult.CustomerNotFound() : CustomerCodeRequestResult.NotEligible();
        store.CreatePendingAsync(Document, Arg.Any<CancellationToken>()).Returns(result);
        var response = await RequestCode.ExecuteAsync(Document);
        Assert.That(response.ChallengeId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(response.RetryAfterSeconds, Is.Zero);
        Assert.That(sender.ReceivedCalls(), Is.Empty);
        await store.DidNotReceive().ActivateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RateLimitDoesNotSendOrActivate()
    {
        store.CreatePendingAsync(Document, Arg.Any<CancellationToken>()).Returns(CustomerCodeRequestResult.RateLimited(42));
        Assert.That((await RequestCode.ExecuteAsync(Document)).RetryAfterSeconds, Is.EqualTo(42));
        Assert.That(sender.ReceivedCalls(), Is.Empty);
        await store.DidNotReceive().ActivateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PersistThenSendThenActivateWithoutReactivatingReplacedChallenge(bool activated)
    {
        var sequence = new List<string>();
        store.CreatePendingAsync(Document, Arg.Any<CancellationToken>()).Returns(_ => { sequence.Add("persisted"); return pending; });
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { sequence.Add("captured"); return Task.CompletedTask; });
        store.ActivateAsync(pending.Customer!.Id, pending.ChallengeId, Arg.Any<CancellationToken>())
            .Returns(_ => { sequence.Add("activation"); return activated; });
        var response = await RequestCode.ExecuteAsync(Document);
        Assert.That(sequence, Is.EqualTo(new[] { "persisted", "captured", "activation" }));
        Assert.That(response.ChallengeId, Is.EqualTo(pending.ChallengeId));
        Assert.That(response.ToString(), Does.Not.Contain("01234567").And.Not.Contain("customer@example.invalid"));
        await sender.Received(1).SendAsync(pending.Customer.Name, pending.Email!, pending.Code!, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendingFailureInvalidatesAndPreservesTheOriginalException()
    {
        var original = new InvalidOperationException("test-only-original");
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(original));
        var error = Assert.ThrowsAsync<InvalidOperationException>(() => RequestCode.ExecuteAsync(Document));
        Assert.That(error, Is.SameAs(original));
        await store.Received(1).InvalidateAsync(pending.Customer!.Id, pending.ChallengeId, Arg.Any<CancellationToken>());
        await store.DidNotReceive().ActivateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void InvalidationOrDiagnosticFailureDoesNotMaskTheSendFailure()
    {
        var original = new TimeoutException("original");
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(original));
        store.InvalidateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new IOException("cleanup")));
        var useCase = new RequestCustomerCodeUseCase(normalizer, store, sender, _ => throw new InvalidOperationException("logging"));
        Assert.That(Assert.ThrowsAsync<TimeoutException>(() => useCase.ExecuteAsync(Document)), Is.SameAs(original));
    }

    [Test]
    public async Task FailedActivationIsTechnicalAndInvalidatesWhenPossible()
    {
        var error = new IOException("commit failure");
        store.ActivateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(error));
        Assert.That(Assert.ThrowsAsync<IOException>(() => RequestCode.ExecuteAsync(Document)), Is.SameAs(error));
        await store.Received(1).InvalidateAsync(pending.Customer!.Id, pending.ChallengeId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VerificationReturnsOnlyTheResultOfAtomicConsumption()
    {
        var result = CustomerAuthenticationResult.Authenticated("test.jwt.token");
        store.VerifyAndConsumeAsync(pending.ChallengeId, Document, pending.Code!, tokens, Arg.Any<CancellationToken>()).Returns(result);
        Assert.That(await VerifyCode.ExecuteAsync(pending.ChallengeId, Document, pending.Code), Is.SameAs(result));
        tokens.DidNotReceiveWithAnyArgs().Generate(default!);
    }

    [Test]
    public async Task InvalidVerificationDoesNotCallPersistence()
    {
        Assert.That((await VerifyCode.ExecuteAsync(Guid.Empty, Document, "01234567")).Outcome, Is.EqualTo(CustomerAuthenticationOutcome.InvalidDocument));
        Assert.That((await VerifyCode.ExecuteAsync(pending.ChallengeId, Document, "invalid")).Outcome, Is.EqualTo(CustomerAuthenticationOutcome.InvalidDocument));
        Assert.That(store.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public async Task CancellationDoesNotStartAnyDependency()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => RequestCode.ExecuteAsync(Document, canceled.Token));
        Assert.CatchAsync<OperationCanceledException>(() => VerifyCode.ExecuteAsync(pending.ChallengeId, Document, pending.Code, canceled.Token));
        Assert.That(store.ReceivedCalls(), Is.Empty);
        Assert.That(sender.ReceivedCalls(), Is.Empty);
        await Task.CompletedTask;
    }
}
