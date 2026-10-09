namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public sealed class CustomerAuthenticationResult
{
    public CustomerAuthenticationOutcome Outcome { get; }
    public string? Token { get; }
    
    private CustomerAuthenticationResult(CustomerAuthenticationOutcome outcome, string? token = null)
    {
        Outcome = outcome;
        Token = token;
    }

    public static CustomerAuthenticationResult InvalidDocument() => new(CustomerAuthenticationOutcome.InvalidDocument);
    public static CustomerAuthenticationResult NotEligible() => new(CustomerAuthenticationOutcome.NotEligible);

    public static CustomerAuthenticationResult Authenticated(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return new CustomerAuthenticationResult(CustomerAuthenticationOutcome.Authenticated, token);
    }
}