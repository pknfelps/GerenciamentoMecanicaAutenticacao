using Amazon.Lambda.APIGatewayEvents;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Documents;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public static class CustomerCodeRequestParser
{
    private const int MaximumBodyBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParse(APIGatewayProxyRequest request, string route, out CustomerCodeInput? input, out bool invalidDocument)
    {
        input = null;
        invalidDocument = false;
        if (!HasJsonContentType(request) || string.IsNullOrEmpty(request.Body)) return false;
        try
        {
            var bytes = ReadBody(request);
            if (bytes is null) return false;
            using var json = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 8 });
            var fields = ReadFields(json.RootElement, route);
            if (fields is null) return false;
            var document = new CustomerDocumentNormalizer().Normalize(fields["document"]);
            if (document is null)
            {
                invalidDocument = true;
                return false;
            }
            input = BuildInput(fields, route, document);
            return input is not null;
        }
        catch (Exception error) when (error is JsonException or FormatException or DecoderFallbackException or EncoderFallbackException or InvalidOperationException)
        { return false; }
    }

    private static byte[]? ReadBody(APIGatewayProxyRequest request)
    {
        // Bound the encoded representation before allocating the decoded buffer.
        var maximumCharacters = request.IsBase64Encoded ? 4 * ((MaximumBodyBytes + 2) / 3) : MaximumBodyBytes;
        if (request.Body.Length > maximumCharacters) return null;
        var bytes = request.IsBase64Encoded ? Convert.FromBase64String(request.Body) : StrictUtf8.GetBytes(request.Body);
        return bytes.Length <= MaximumBodyBytes ? bytes : null;
    }

    private static Dictionary<string, string>? ReadFields(JsonElement root, string route)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var expected = route == AuthenticationRoute.RequestCode ? new[] { "document" } : new[] { "document", "challengeId", "code" };
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in root.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(field.Name, field.Value.GetString()!)) return null;
        }
        return fields.Count == expected.Length && fields.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected) ? fields : null;
    }

    private static CustomerCodeInput? BuildInput(Dictionary<string, string> fields, string route, string document)
    {
        if (route == AuthenticationRoute.RequestCode) return new CustomerCodeInput(document, Guid.Empty, null);
        var code = fields["code"];
        return Guid.TryParseExact(fields["challengeId"], "D", out var id) && id != Guid.Empty && code.Length == 8 && code.All(char.IsAsciiDigit)
            ? new CustomerCodeInput(document, id, code) : null;
    }

    private static bool HasJsonContentType(APIGatewayProxyRequest request)
    {
        var single = request.Headers?.Where(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Select(header => header.Value) ?? [];
        var multiple = request.MultiValueHeaders?.Where(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).SelectMany(header => header.Value ?? [string.Empty]) ?? [];
        var values = single.Concat(multiple).ToArray();
        return values.Length > 0 && values.All(IsJsonContentType);
    }

    private static bool IsJsonContentType(string value) => MediaTypeHeaderValue.TryParse(value, out var media)
        && string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        && media.Parameters.Count <= 1 && media.Parameters.All(parameter => parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parameter.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
}
