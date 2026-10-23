namespace GerenciamentoMecanica.Autenticacao.Function.Http;

public sealed class CustomerCodeInput(string document, Guid challengeId, string? code)
{
    public string Document { get; } = document;
    public Guid ChallengeId { get; } = challengeId;
    public string? Code { get; } = code;
}
