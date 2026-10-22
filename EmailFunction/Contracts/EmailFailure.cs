namespace GerenciamentoMecanica.Serverless.EmailFunction.Contracts;

public enum EmailFailure
{
    InvalidRequest,
    SmtpUnavailable,
    Timeout,
    TechnicalFailure
}
