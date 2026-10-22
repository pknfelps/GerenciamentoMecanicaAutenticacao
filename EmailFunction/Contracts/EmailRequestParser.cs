using GerenciamentoMecanica.Serverless.EmailFunction.Validation;
using System.Text;
using System.Text.Json;

namespace GerenciamentoMecanica.Serverless.EmailFunction.Contracts;

public static class EmailRequestParser
{
    public const int MaximumPayloadBytes = 128 * 1024;
    public const int MaximumTextBytes = 32 * 1024;
    public const int MaximumHtmlBytes = 64 * 1024;

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "version", "type", "toName", "toEmail", "subject", "textBody", "htmlBody"
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<EmailRequest?> ParseAsync(Stream input, CancellationToken cancellationToken)
    {
        // Um byte extra distingue o limite exato de um envelope maior, sem ler tudo em memoria.
        var buffer = new byte[MaximumPayloadBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
        }

        if (length == 0 || length > MaximumPayloadBytes)
            return null;

        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            return ParseObject(json.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or EncoderFallbackException)
        {
            return null;
        }
    }

    private static EmailRequest? ParseObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name) || !seen.Add(property.Name))
                return null;
        }

        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
            !TryString(root, "type", out var type) || type is not ("customer_access_code" or "budget_available" or "status_changed") ||
            !TryString(root, "toName", out var toName) || !MailboxValidation.IsSafeHeader(toName, 100) ||
            !TryString(root, "toEmail", out var toEmail) || !MailboxValidation.IsAddress(toEmail) ||
            !TryString(root, "subject", out var subject) || string.IsNullOrWhiteSpace(subject) || !MailboxValidation.IsSafeHeader(subject, 200) ||
            !TryString(root, "textBody", out var text) || string.IsNullOrWhiteSpace(text) || StrictUtf8.GetByteCount(text) > MaximumTextBytes)
            return null;

        string? html = null;
        if (root.TryGetProperty("htmlBody", out var htmlValue) && htmlValue.ValueKind != JsonValueKind.Null)
        {
            if (htmlValue.ValueKind != JsonValueKind.String)
                return null;
            html = htmlValue.GetString();
            if (StrictUtf8.GetByteCount(html!) > MaximumHtmlBytes)
                return null;
        }

        return new EmailRequest(type, toName, toEmail, subject, text, html);
    }

    private static bool TryString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString()!;
        return true;
    }
}
