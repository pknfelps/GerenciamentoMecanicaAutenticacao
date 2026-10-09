using GerenciamentoMecanica.Auth.Contracts;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Authentication;

public sealed class CustomerTokenIssuer : ICustomerTokenIssuer
{
    private readonly JwtIssuer issuer;

    public CustomerTokenIssuer(JwtConfiguration configuration, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        
        issuer = new JwtIssuer(configuration.Key, configuration.Issuer, configuration.Audience, timeProvider);
    }

    public string Generate(CustomerIdentity customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        if (!customer.IsEligible)
            throw new InvalidOperationException("Customer is not eligible for authentication.");

        try
        {
            if (DocumentRules.Normalize(customer.Document) != customer.Document)
                throw new InvalidDataException("Customer document is not canonical.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Customer document is invalid.");
        }

        return issuer.Generate(customer.Name, "Customer", customer.Id);
    }
}