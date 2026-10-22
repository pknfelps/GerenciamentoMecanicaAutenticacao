using Amazon.Lambda;
using Amazon.Lambda.Model;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using System.Net;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Email;

public sealed class LambdaCustomerCodeSender(IAmazonLambda lambda, string functionArn) : ICustomerCodeSender
{
    public async Task SendAsync(string name, string email, string code, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var payload = Message(name, email, code);
            var response = await lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionArn,
                InvocationType = InvocationType.RequestResponse,
                LogType = LogType.None,
                PayloadStream = payload
            }, deadline.Token).WaitAsync(deadline.Token);
            using var responsePayload = response.Payload;
            deadline.Token.ThrowIfCancellationRequested();
            EnsureCaptured(response);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException("Envio cancelado ou prazo excedido.", deadline.Token); }
        catch (TimeoutException) { throw new TimeoutException("Prazo de captura da mensagem excedido."); }
        catch (Exception) { throw new EmailInvocationException(); }
    }

    private static MemoryStream Message(string name, string email, string code) => new(JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        type = "customer_access_code",
        toName = name,
        toEmail = email,
        subject = "Código de acesso",
        textBody = $"Olá, {name}. Seu código de acesso é {code}. Ele expira em cinco minutos.",
        htmlBody = $"<p>Olá, {WebUtility.HtmlEncode(name)}.</p><p>Seu código de acesso é <strong>{WebUtility.HtmlEncode(code)}</strong>. Ele expira em cinco minutos.</p>"
    }));

    private static void EnsureCaptured(InvokeResponse response)
    {
        // The SDK already materializes a MemoryStream. Bound it before parsing/copying or logging anything.
        if (response.StatusCode != 200 || !string.IsNullOrEmpty(response.FunctionError) || response.Payload is not { Length: > 0 and <= 1024 })
            throw new EmailInvocationException();
        using var json = JsonDocument.Parse(response.Payload.ToArray());
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || !root.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String)
            throw new EmailInvocationException();
        if (outcome.GetString() == "captured" && HasExactly(root, ["version", "outcome"]))
            return;
        if (outcome.GetString() == "failure" && HasExactly(root, ["version", "outcome", "error"])
            && root.GetProperty("error").ValueKind == JsonValueKind.String && root.GetProperty("error").GetString() == "timeout")
            throw new TimeoutException("Prazo de captura da mensagem excedido.");
        throw new EmailInvocationException();
    }

    private static bool HasExactly(JsonElement root, string[] expected)
    {
        var fields = root.EnumerateObject().ToArray();
        return fields.Length == expected.Length && fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }
}
