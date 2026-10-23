using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;

internal sealed class AccessChallengeRow
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public byte[] Salt { get; set; } = [];
    public byte[] Verifier { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string State { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? InvalidatedAt { get; set; }

    public CustomerAccessChallenge ToDomain()
    {
        var state = State switch
        {
            "Pending" => CustomerAccessChallengeState.Pending,
            "Ready" => CustomerAccessChallengeState.Ready,
            "Consumed" => CustomerAccessChallengeState.Consumed,
            "Invalidated" => CustomerAccessChallengeState.Invalidated,
            _ => throw new InvalidDataException("Stored challenge state is invalid.")
        };
        try
        {
            return new CustomerAccessChallenge(Id, CustomerId, Salt, Verifier, CreatedAt, ExpiresAt,
                state, FailedAttempts, ConsumedAt, InvalidatedAt);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("Stored challenge data is invalid.");
        }
    }
}
