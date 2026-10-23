using Amazon.Lambda.APIGatewayEvents;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using System.Globalization;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public static class AuthenticationResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static APIGatewayProxyResponse FromRequest(CustomerCodeRequestResponse result)
    {
        if (result.InvalidDocument) return InvalidRequest(AuthenticationRoute.RequestCode);
        if (result.RetryAfterSeconds > 0)
        {
            var response = Problem(429, "Limite de solicitações atingido", "Aguarde antes de solicitar outro código.", AuthenticationRoute.RequestCode);
            response.Headers["Retry-After"] = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return response;
        }
        return Create(202, "application/json", JsonSerializer.Serialize(new
        { challengeId = result.ChallengeId, message = "Se o cadastro estiver apto, um código será enviado." }, JsonOptions));
    }
    public static APIGatewayProxyResponse FromResult(CustomerAuthenticationResult result) => result.Outcome switch
    {
        CustomerAuthenticationOutcome.Authenticated => Create(200, "application/json", JsonSerializer.Serialize(result.Token, JsonOptions)),
        CustomerAuthenticationOutcome.InvalidDocument => InvalidRequest(AuthenticationRoute.VerifyCode),
        CustomerAuthenticationOutcome.NotEligible => Problem(401, "Autenticação não autorizada", "Não foi possível autenticar o cliente.", AuthenticationRoute.VerifyCode),
        _ => throw new InvalidOperationException("Unexpected authentication outcome.")
    };
    public static APIGatewayProxyResponse InvalidRequest(string route) => Problem(400, "Requisição inválida", "O corpo ou os campos informados são inválidos.", route);
    public static APIGatewayProxyResponse TechnicalFailure(string route) => Problem(500, "Erro interno do servidor", "Não foi possível processar a autenticação.", route);
    public static APIGatewayProxyResponse NotFound() => Problem(404, "Rota não encontrada", "A rota solicitada não existe.", AuthenticationRoute.Unknown);
    public static APIGatewayProxyResponse MethodNotAllowed(string route)
    {
        var response = Problem(405, "Método não permitido", "Use o método POST.", route);
        response.Headers["Allow"] = "POST";
        return response;
    }
    private static APIGatewayProxyResponse Problem(int status, string title, string detail, string route) =>
        Create(status, "application/problem+json", JsonSerializer.Serialize(new AuthenticationProblem(status, title, detail, route), JsonOptions));
    private static APIGatewayProxyResponse Create(int status, string contentType, string body) => new()
    {
        StatusCode = status,
        Body = body,
        IsBase64Encoded = false,
        Headers = new Dictionary<string, string> { ["Content-Type"] = contentType, ["Cache-Control"] = "no-store" }
    };
}
