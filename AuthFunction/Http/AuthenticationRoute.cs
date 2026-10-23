using Amazon.Lambda.APIGatewayEvents;
namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public static class AuthenticationRoute
{
    public const string RequestCode = "/customers/auth/request-code";
    public const string VerifyCode = "/customers/auth/verify-code";
    public const string Unknown = "/customers/auth";
    public static string Resolve(APIGatewayProxyRequest request) => (request.Resource ?? request.Path) switch
    { RequestCode => RequestCode, VerifyCode => VerifyCode, _ => Unknown };
}
