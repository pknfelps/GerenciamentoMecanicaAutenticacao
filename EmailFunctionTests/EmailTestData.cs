using Amazon.Lambda.Core;
using GerenciamentoMecanica.Serverless.EmailFunction.Configuration;
using NSubstitute;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmailFunctionTests;

internal static class EmailTestData
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Message() => new()
    {
        ["version"] = 1,
        ["type"] = "customer_access_code",
        ["toName"] = "Cliente de teste",
        ["toEmail"] = "customer@example.invalid",
        ["subject"] = "Código de acesso",
        ["textBody"] = "Código fictício: 01234567.",
        ["htmlBody"] = "<p>Código fictício: <strong>01234567</strong>.</p>"
    };

    public static MemoryStream Payload(JsonObject? message = null) =>
        new(Encoding.UTF8.GetBytes((message ?? Message()).ToJsonString(JsonOptions)));

    public static ILambdaContext Context(ILambdaLogger? logger = null, TimeSpan? remaining = null)
    {
        var context = Substitute.For<ILambdaContext>();
        context.AwsRequestId.Returns("email-request-id");
        context.RemainingTime.Returns(remaining ?? TimeSpan.FromSeconds(6));
        context.Logger.Returns(logger ?? Substitute.For<ILambdaLogger>());
        return context;
    }

    public static SmtpSettings Settings(string host, int port) => SmtpSettings.Load(key => key switch
    {
        "Email__SmtpHost" => host,
        "Email__SmtpPort" => port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    });
}
