namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public interface ICustomerDocumentNormalizer
{
    // Null represents invalid input; no technical failure is converted to invalid input.
    string? Normalize(string? document);
}