using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

// Plain classes intentionally avoid a record-generated ToString containing code/email.
public sealed class CustomerCodeRequestResult
{
    public CustomerCodeRequestOutcome Outcome { get; }
    public Guid ChallengeId { get; }
    public CustomerIdentity? Customer { get; }
    public string? Email { get; }
    public string? Code { get; }
    public int RetryAfterSeconds { get; }

    private CustomerCodeRequestResult(CustomerCodeRequestOutcome outcome, Guid challengeId,
        CustomerIdentity? customer = null, string? email = null, string? code = null, int retryAfterSeconds = 0)
    {
        Outcome = outcome;
        ChallengeId = challengeId;
        Customer = customer;
        Email = email;
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public static CustomerCodeRequestResult Pending(Guid id, CustomerIdentity customer, string email, string code) =>
        new(CustomerCodeRequestOutcome.Pending, id, customer, email, code);
    public static CustomerCodeRequestResult CustomerNotFound() => new(CustomerCodeRequestOutcome.CustomerNotFound, Guid.NewGuid());
    public static CustomerCodeRequestResult NotEligible() => new(CustomerCodeRequestOutcome.NotEligible, Guid.NewGuid());
    public static CustomerCodeRequestResult RateLimited(int retryAfterSeconds) =>
        new(CustomerCodeRequestOutcome.RateLimited, Guid.NewGuid(), retryAfterSeconds: retryAfterSeconds);
}
