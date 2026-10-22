namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public sealed class AuthenticationProblem(int status, string title, string detail, string route)
{
    public string Type => "about:blank";
    public string Title { get; } = title;
    public int Status { get; } = status;
    public string Detail { get; } = detail;
    public string Instance { get; } = route;
}
