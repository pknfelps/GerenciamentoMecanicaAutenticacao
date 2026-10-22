using GerenciamentoMecanica.Serverless.EmailFunction.Validation;
using System.Globalization;

namespace GerenciamentoMecanica.Serverless.EmailFunction.Configuration;

public sealed class SmtpSettings
{
    public const string InvalidConfigurationMessage = "Configuracao de envio de email invalida.";

    private SmtpSettings(string host, int port, string senderName, string senderEmail)
    {
        Host = host;
        Port = port;
        SenderName = senderName;
        SenderEmail = senderEmail;
    }

    public string Host { get; }
    public int Port { get; }
    public string SenderName { get; }
    public string SenderEmail { get; }

    public static SmtpSettings Load(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var environment = read("Runtime__Environment");
        var host = read("Email__SmtpHost");
        var rawPort = read("Email__SmtpPort") ?? "25";
        var senderName = read("Email__SenderName") ?? "Mecânica";
        var senderEmail = read("Email__SenderEmail") ?? "noreply@mecanica.local";

        if ((environment is not null && environment is not ("hom" or "prd")) ||
            (environment is not null && read("AWS_REGION") != "us-east-1") ||
            string.IsNullOrWhiteSpace(host) || host != host.Trim() ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535 ||
            !MailboxValidation.IsSafeHeader(senderName, 100) || !MailboxValidation.IsAddress(senderEmail))
            throw new InvalidOperationException(InvalidConfigurationMessage);

        return new SmtpSettings(host, port, senderName, senderEmail);
    }
}
