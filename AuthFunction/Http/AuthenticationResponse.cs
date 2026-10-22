using Amazon.Lambda.APIGatewayEvents;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public static class AuthenticationResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static APIGatewayProxyResponse FromResult(CustomerAuthenticationResult result) =>
        result.Outcome switch
        {
            CustomerAuthenticationOutcome.Authenticated =>
                Create(200, "application/json", JsonSerializer.Serialize(result.Token, JsonOptions)),
            CustomerAuthenticationOutcome.InvalidDocument => InvalidRequest(),
            CustomerAuthenticationOutcome.NotEligible => Problem(401, "Autenticação não autorizada",
                "Não foi possível autenticar o cliente."),
            _ => throw new InvalidOperationException("Unexpected authentication outcome.")
        };

    public static APIGatewayProxyResponse InvalidRequest() =>
        Problem(400, "Requisição inválida", "O corpo ou documento informado é inválido.");

    public static APIGatewayProxyResponse TechnicalFailure() =>
        Problem(500, "Erro interno do servidor", "Não foi possível processar a autenticação.");

    private static APIGatewayProxyResponse Problem(int status, string title, string detail) =>
        Create(status, "application/problem+json",
            JsonSerializer.Serialize(new AuthenticationProblem(status, title, detail), JsonOptions));

    private static APIGatewayProxyResponse Create(int status, string contentType, string body) => new()
    {
        StatusCode = status,
        Body = body,
        IsBase64Encoded = false,
        Headers = new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            ["Cache-Control"] = "no-store"
        }
    };
}