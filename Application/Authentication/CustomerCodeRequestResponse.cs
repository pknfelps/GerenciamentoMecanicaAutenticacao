namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

// Public result contains no code, email or customer identity.
public sealed class CustomerCodeRequestResponse
{
    public Guid ChallengeId { get; }
    public int RetryAfterSeconds { get; }
    public bool InvalidDocument { get; }
    private CustomerCodeRequestResponse(Guid id, int retryAfter = 0, bool invalidDocument = false)
    { ChallengeId = id; RetryAfterSeconds = retryAfter; InvalidDocument = invalidDocument; }
    public static CustomerCodeRequestResponse Accepted(Guid id) => new(id);
    public static CustomerCodeRequestResponse RateLimited(int seconds) => new(Guid.Empty, seconds);
    public static CustomerCodeRequestResponse Invalid() => new(Guid.Empty, invalidDocument: true);
}
