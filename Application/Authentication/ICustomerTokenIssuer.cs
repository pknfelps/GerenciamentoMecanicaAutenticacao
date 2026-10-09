using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public interface ICustomerTokenIssuer
{
    string Generate(CustomerIdentity customer);
}