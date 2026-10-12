using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public interface ICustomerLookup
{
    Task<CustomerIdentity?> FindByDocumentAsync(string canonicalDocument, CancellationToken cancellationToken);
}