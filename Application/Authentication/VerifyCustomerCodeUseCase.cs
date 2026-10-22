namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public sealed class VerifyCustomerCodeUseCase(ICustomerDocumentNormalizer normalizer,
    ICustomerAccessStore store, ICustomerTokenIssuer tokens)
{
    public async Task<CustomerAuthenticationResult> ExecuteAsync(Guid challengeId, string? document, string? code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = normalizer.Normalize(document);
        if (canonical is null || challengeId == Guid.Empty || code is not { Length: 8 } || !code.All(char.IsAsciiDigit))
            return CustomerAuthenticationResult.InvalidDocument();
        return await store.VerifyAndConsumeAsync(challengeId, canonical, code, tokens, cancellationToken);
    }
}
