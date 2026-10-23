using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;

internal sealed class AccessRequestLimitRow
{
    public DateTime WindowStartedAt { get; set; }
    public DateTime WindowExpiresAt { get; set; }
    public int RequestCount { get; set; }
    public DateTime LastRequestedAt { get; set; }

    public CustomerAccessRequestLimit ToDomain()
    {
        try { return new CustomerAccessRequestLimit(WindowStartedAt, WindowExpiresAt, RequestCount, LastRequestedAt); }
        catch (ArgumentException) { throw new InvalidDataException("Stored customer request limit is invalid."); }
    }
}
