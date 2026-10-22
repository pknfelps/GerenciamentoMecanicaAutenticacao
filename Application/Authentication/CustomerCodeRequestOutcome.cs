namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public enum CustomerCodeRequestOutcome
{
    Pending,
    CustomerNotFound,
    NotEligible,
    RateLimited
}
