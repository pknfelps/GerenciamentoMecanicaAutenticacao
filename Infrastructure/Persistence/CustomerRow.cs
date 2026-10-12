namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;

internal sealed class CustomerRow
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Document { get; set; }
    public string? Status { get; set; }
}