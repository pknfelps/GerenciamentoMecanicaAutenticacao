namespace GerenciamentoMecanica.Serverless.EmailFunction.Contracts;

public sealed class EmailRequest
{
    internal EmailRequest(string type, string toName, string toEmail, string subject, string textBody, string? htmlBody)
    {
        Type = type;
        ToName = toName;
        ToEmail = toEmail;
        Subject = subject;
        TextBody = textBody;
        HtmlBody = htmlBody;
    }

    public string Type { get; }
    public string ToName { get; }
    public string ToEmail { get; }
    public string Subject { get; }
    public string TextBody { get; }
    public string? HtmlBody { get; }
}
