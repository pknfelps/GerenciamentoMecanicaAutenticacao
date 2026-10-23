using GerenciamentoMecanica.Serverless.EmailFunction.Contracts;

namespace GerenciamentoMecanica.Serverless.EmailFunction.Smtp;

public interface ISmtpEmailSender
{
    Task SendAsync(EmailRequest request, CancellationToken cancellationToken);
}
