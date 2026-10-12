using System.Text.RegularExpressions;

namespace GerenciamentoMecanica.Autenticacao.Domain.Customers;

public sealed class CustomerIdentity
{
    public Guid Id { get; }
    public string Name { get; }
    public string Document { get; }
    public CustomerStatus Status { get; }
    public bool IsEligible => Status == CustomerStatus.Active;

    public CustomerIdentity(Guid id, string name, string document, CustomerStatus status)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Customer ID is required.", nameof(id));

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(document);

        if (!Regex.IsMatch(document,
                @"\A(?:[0-9]{3}\.[0-9]{3}\.[0-9]{3}-[0-9]{2}|[0-9]{2}\.[0-9]{3}\.[0-9]{3}/[0-9]{4}-[0-9]{2})\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("Customer document must use its canonical format.", nameof(document));

        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        Id = id;
        Name = name;
        Document = document;
        Status = status;
    }
}