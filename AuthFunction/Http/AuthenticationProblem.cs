namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public sealed class AuthenticationProblem
{
    public string Type { get; } = "about:blank";
    public string Title { get; }
    public int Status { get; }
    public string Detail { get; }
    public string Instance { get; } = "/customers/validate";

    public AuthenticationProblem(int status, string title, string detail)
    {
        Status = status;
        Title = title;
        Detail = detail;
    }
}