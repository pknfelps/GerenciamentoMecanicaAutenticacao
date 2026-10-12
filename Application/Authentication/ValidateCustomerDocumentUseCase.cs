namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public sealed class ValidateCustomerDocumentUseCase(
    ICustomerDocumentNormalizer normalizer,
    ICustomerLookup customers,
    ICustomerTokenIssuer tokens)
{
    public async Task<CustomerAuthenticationResult> ExecuteAsync(
        string? document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalDocument = normalizer.Normalize(document);

        if (canonicalDocument is null)
            return CustomerAuthenticationResult.InvalidDocument();

        cancellationToken.ThrowIfCancellationRequested();

        var customer = await customers.FindByDocumentAsync(canonicalDocument, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (customer is null || !customer.IsEligible)
            return CustomerAuthenticationResult.NotEligible();

        if (customer.Document != canonicalDocument)
            throw new InvalidDataException("Customer identity does not match the requested document.");

        return CustomerAuthenticationResult.Authenticated(tokens.Generate(customer));
    }
}