using GerenciamentoMecanica.Serverless.EmailFunction.Configuration;
using GerenciamentoMecanica.Serverless.EmailFunction.Contracts;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GerenciamentoMecanica.Serverless.EmailFunction.Smtp;

public sealed class SmtpEmailSender(SmtpSettings settings) : ISmtpEmailSender
{
    public async Task SendAsync(EmailRequest request, CancellationToken cancellationToken)
    {
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.SenderName, settings.SenderEmail));
        message.To.Add(new MailboxAddress(request.ToName, request.ToEmail));
        message.Subject = request.Subject;
        message.Date = DateTimeOffset.UtcNow;
        message.Body = new BodyBuilder { TextBody = request.TextBody, HtmlBody = request.HtmlBody }.ToMessageBody();

        // Uma conexao por chamada. Sem credenciais, ProtocolLogger ou retentativa propria.
        using var client = new SmtpClient { Timeout = 3000 };
        await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.None, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        // Dispose fecha o socket sem esperar outro comando apos a aceitacao do DATA.
    }
}
