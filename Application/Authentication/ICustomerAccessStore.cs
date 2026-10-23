namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public interface ICustomerAccessStore
{
    Task<CustomerCodeRequestResult> CreatePendingAsync(string canonicalDocument, CancellationToken cancellationToken = default);
    Task<bool> ActivateAsync(Guid customerId, Guid challengeId, CancellationToken cancellationToken = default);
    Task<bool> InvalidateAsync(Guid customerId, Guid challengeId, CancellationToken cancellationToken = default);
    Task<CustomerAuthenticationResult> VerifyAndConsumeAsync(Guid challengeId, string canonicalDocument, string code,
        ICustomerTokenIssuer tokenIssuer, CancellationToken cancellationToken = default);
}
