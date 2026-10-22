using System.Text.Json.Serialization;

namespace GerenciamentoMecanica.Serverless.EmailFunction.Contracts;

public sealed class EmailResponse
{
    private EmailResponse(string outcome, string? error)
    {
        Outcome = outcome;
        Error = error;
    }

    [JsonPropertyName("version")]
    public int Version => 1;

    [JsonPropertyName("outcome")]
    public string Outcome { get; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; }

    public static EmailResponse Captured() => new("captured", null);

    public static EmailResponse Failure(EmailFailure error) => new("failure", error switch
    {
        EmailFailure.InvalidRequest => "invalid-request",
        EmailFailure.SmtpUnavailable => "smtp-unavailable",
        EmailFailure.Timeout => "timeout",
        _ => "technical-failure"
    });
}
