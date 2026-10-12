using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using NSubstitute;

namespace GerenciamentoMecanica.Autenticacao.ApplicationTests;

public class ValidateCustomerDocumentUseCaseTests
{
    private const string Document = "529.982.247-25";
    private ICustomerDocumentNormalizer normalizer = null!;
    private ICustomerLookup lookup = null!;
    private ICustomerTokenIssuer tokens = null!;
    private ValidateCustomerDocumentUseCase useCase = null!;
    private CustomerIdentity customer = null!;

    [SetUp]
    public void SetUp()
    {
        normalizer = Substitute.For<ICustomerDocumentNormalizer>();
        lookup = Substitute.For<ICustomerLookup>();
        tokens = Substitute.For<ICustomerTokenIssuer>();
        customer = new CustomerIdentity(Guid.NewGuid(), "Cliente", Document, CustomerStatus.Active);
        normalizer.Normalize(Arg.Any<string?>()).Returns(Document);
        lookup.FindByDocumentAsync(Document, Arg.Any<CancellationToken>()).Returns(customer);
        tokens.Generate(customer).Returns("test-only-token");
        useCase = new ValidateCustomerDocumentUseCase(normalizer, lookup, tokens);
    }

    [Test]
    public async Task AuthenticatesFromStoredIdentityAndCanonicalDocument()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await useCase.ExecuteAsync("52998224725", cancellation.Token);
        Assert.That(result.Outcome, Is.EqualTo(CustomerAuthenticationOutcome.Authenticated));
        Assert.That(result.Token, Is.EqualTo("test-only-token"));
        Assert.That(result.ToString(), Does.Not.Contain(result.Token));
        normalizer.Received(1).Normalize("52998224725");
        await lookup.Received(1).FindByDocumentAsync(Document, cancellation.Token);
        tokens.Received(1).Generate(customer);
    }

    [Test]
    public async Task InvalidDocumentDoesNotQueryOrIssue()
    {
        normalizer.Normalize(Arg.Any<string?>()).Returns((string?)null);
        var result = await useCase.ExecuteAsync(null);
        Assert.That(result.Outcome, Is.EqualTo(CustomerAuthenticationOutcome.InvalidDocument));
        Assert.That(result.Token, Is.Null);
        Assert.That(lookup.ReceivedCalls(), Is.Empty);
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrInactiveCustomerDoesNotIssue(bool inactive)
    {
        lookup.FindByDocumentAsync(Document, Arg.Any<CancellationToken>()).Returns(inactive
            ? new CustomerIdentity(customer.Id, customer.Name, Document, CustomerStatus.Inactive) : null);
        var result = await useCase.ExecuteAsync(Document);
        Assert.That(result.Outcome, Is.EqualTo(CustomerAuthenticationOutcome.NotEligible));
        Assert.That(result.Token, Is.Null);
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void LookupFailureIsTechnical()
    {
        var failure = new InvalidOperationException("test-only-database-failure");
        lookup.FindByDocumentAsync(Document, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CustomerIdentity?>(failure));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(Document)), Is.SameAs(failure));
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void NormalizationFailureIsTechnical()
    {
        normalizer.Normalize(Arg.Any<string?>()).Returns(_ => throw new InvalidOperationException("test-only-normalization-failure"));
        Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(Document));
        Assert.That(lookup.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void TokenFailureIsTechnical()
    {
        tokens.Generate(customer).Returns(_ => throw new InvalidOperationException("test-only-token-failure"));
        Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(Document));
    }

    [Test]
    public void MismatchedCustomerIsTechnical()
    {
        lookup.FindByDocumentAsync(Document, Arg.Any<CancellationToken>()).Returns(
            new CustomerIdentity(customer.Id, customer.Name, "10.359.666/0001-94", CustomerStatus.Active));
        Assert.ThrowsAsync<InvalidDataException>(() => useCase.ExecuteAsync(Document));
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void EmptyTokenDoesNotBecomeSuccessfulAuthentication()
    {
        tokens.Generate(customer).Returns("");
        Assert.ThrowsAsync<ArgumentException>(() => useCase.ExecuteAsync(Document));
    }

    [Test]
    public void PreCanceledCallDoesNoWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => useCase.ExecuteAsync(Document, cancellation.Token));
        Assert.That(normalizer.ReceivedCalls(), Is.Empty);
        Assert.That(lookup.ReceivedCalls(), Is.Empty);
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void CancellationDuringLookupPreventsEmission()
    {
        using var cancellation = new CancellationTokenSource();
        lookup.FindByDocumentAsync(Document, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<CustomerIdentity?>(customer);
        });
        Assert.CatchAsync<OperationCanceledException>(() => useCase.ExecuteAsync(Document, cancellation.Token));
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public void LookupCancellationIsNotAuthenticationFailure()
    {
        lookup.FindByDocumentAsync(Document, Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<CustomerIdentity?>(new CancellationToken(true)));
        Assert.CatchAsync<OperationCanceledException>(() => useCase.ExecuteAsync(Document));
        Assert.That(tokens.ReceivedCalls(), Is.Empty);
    }
}
