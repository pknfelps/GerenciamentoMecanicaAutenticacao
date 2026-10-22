using Amazon.Lambda.APIGatewayEvents;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public static class CustomerValidationRequestParser
{
    public static bool TryParse(APIGatewayProxyRequest request, out string? document)
    {
        document = null;

        if (!HasJsonContentType(request) || string.IsNullOrEmpty(request.Body))
            return false;

        try
        {
            var body = request.IsBase64Encoded
                ? new UTF8Encoding(false, true).GetString(Convert.FromBase64String(request.Body))
                : request.Body;

            using var json = JsonDocument.Parse(body);

            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var properties = json.RootElement.EnumerateObject().ToArray();

            if (properties.Length != 1 || properties[0].Name != "document" ||
                properties[0].Value.ValueKind != JsonValueKind.String)
                return false;

            document = properties[0].Value.GetString();
            return !string.IsNullOrWhiteSpace(document);
        }
        catch (Exception error) when (error is JsonException or FormatException or DecoderFallbackException
                                          or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasJsonContentType(APIGatewayProxyRequest request)
    {
        var singleValues = request.Headers?
            .Where(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value) ?? [];

        var multipleValues = request.MultiValueHeaders?
            .Where(header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value ?? [string.Empty]) ?? [];

        var values = singleValues.Concat(multipleValues).ToArray();
        return values.Length > 0 && values.All(IsJsonContentType);
    }

    private static bool IsJsonContentType(string value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var media) ||
            !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;

        return media.Parameters.Count <= 1 && media.Parameters.All(parameter =>
            parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parameter.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
    }
}