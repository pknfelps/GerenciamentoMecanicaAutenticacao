using GerenciamentoMecanica.Auth.Contracts;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using System.Text.RegularExpressions;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Documents;

public sealed class CustomerDocumentNormalizer : ICustomerDocumentNormalizer
{
    public string? Normalize(string? document)
    {
        if (document is null || document.Length > 18 || !Regex.IsMatch(document,
                @"\A(?:[0-9]{11}|[0-9]{14}|[0-9]{3}\.[0-9]{3}\.[0-9]{3}-[0-9]{2}|[0-9]{2}\.[0-9]{3}\.[0-9]{3}/[0-9]{4}-[0-9]{2})\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            return null;

        try
        {
            return DocumentRules.Normalize(document);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}