using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;

internal sealed class CustomerRow
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Document { get; set; }
    public string? Status { get; set; }
    public string? Email { get; set; }

    public CustomerIdentity ToIdentity()
    {
        var status = Status switch
        {
            "Active" => CustomerStatus.Active,
            "Inactive" => CustomerStatus.Inactive,
            _ => throw new InvalidDataException("Customer status in the database is invalid.")
        };
        try { return new CustomerIdentity(Id, Name!, Document!, status); }
        catch (ArgumentException) { throw new InvalidDataException("Customer identity in the database is invalid."); }
    }
}
