namespace GerenciamentoMecanica.Autenticacao.Domain.Customers;

public sealed class CustomerAccessChallenge
{
    public Guid Id { get; }
    public Guid CustomerId { get; }
    public byte[] Salt { get; }
    public byte[] Verifier { get; }
    public DateTime CreatedAt { get; }
    public DateTime ExpiresAt { get; }
    public CustomerAccessChallengeState State { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public DateTime? InvalidatedAt { get; private set; }

    public CustomerAccessChallenge(Guid id, Guid customerId, byte[] salt, byte[] verifier,
        DateTime createdAt, DateTime expiresAt, CustomerAccessChallengeState state,
        int failedAttempts = 0, DateTime? consumedAt = null, DateTime? invalidatedAt = null)
    {
        if (id == Guid.Empty || customerId == Guid.Empty || salt?.Length != 16 || verifier?.Length != 32)
            throw new ArgumentException("Challenge identity or verifier is invalid.");
        RequireUtc(createdAt);
        RequireUtc(expiresAt);
        if (expiresAt - createdAt != TimeSpan.FromMinutes(5) || !Enum.IsDefined(state)
            || failedAttempts is < 0 or > 5
            || (failedAttempts == 5 && state != CustomerAccessChallengeState.Invalidated)
            || (state == CustomerAccessChallengeState.Pending && failedAttempts != 0))
            throw new ArgumentException("Challenge state or lifetime is invalid.");

        if (consumedAt is { } consumed)
        {
            RequireUtc(consumed);
            if (consumed < createdAt || consumed >= expiresAt)
                throw new ArgumentException("Challenge consumption time is invalid.");
        }
        if (invalidatedAt is { } invalidated)
        {
            RequireUtc(invalidated);
            if (invalidated < createdAt)
                throw new ArgumentException("Challenge invalidation time is invalid.");
        }
        if ((state == CustomerAccessChallengeState.Consumed) != consumedAt.HasValue
            || (state == CustomerAccessChallengeState.Invalidated) != invalidatedAt.HasValue)
            throw new ArgumentException("Challenge terminal state is invalid.");

        Id = id;
        CustomerId = customerId;
        Salt = salt.ToArray();
        Verifier = verifier.ToArray();
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        State = state;
        FailedAttempts = failedAttempts;
        ConsumedAt = consumedAt;
        InvalidatedAt = invalidatedAt;
    }

    public bool IsOpen => State is CustomerAccessChallengeState.Pending or CustomerAccessChallengeState.Ready;
    public bool CanVerify(DateTime now) => State == CustomerAccessChallengeState.Ready && IsWithinLifetime(now);

    public bool Activate(DateTime now)
    {
        if (State != CustomerAccessChallengeState.Pending || !IsWithinLifetime(now))
            return false;
        State = CustomerAccessChallengeState.Ready;
        return true;
    }

    public bool Invalidate(DateTime now)
    {
        RequireCurrentTime(now);
        if (!IsOpen)
            return false;
        State = CustomerAccessChallengeState.Invalidated;
        InvalidatedAt = now;
        return true;
    }

    public void RejectAttempt(DateTime now)
    {
        if (!CanVerify(now))
            throw new InvalidOperationException("Challenge cannot accept a verification attempt.");
        FailedAttempts++;
        if (FailedAttempts == 5)
            Invalidate(now);
    }

    public void Consume(DateTime now)
    {
        if (!CanVerify(now))
            throw new InvalidOperationException("Challenge cannot be consumed.");
        State = CustomerAccessChallengeState.Consumed;
        ConsumedAt = now;
    }

    private bool IsWithinLifetime(DateTime now)
    {
        RequireCurrentTime(now);
        return now < ExpiresAt;
    }

    private void RequireCurrentTime(DateTime now)
    {
        RequireUtc(now);
        if (now < CreatedAt)
            throw new ArgumentException("Current time precedes challenge creation.");
    }

    internal static void RequireUtc(DateTime time)
    {
        if (time.Kind != DateTimeKind.Utc || time.Ticks == DateTime.MinValue.Ticks || time.Ticks == DateTime.MaxValue.Ticks)
            throw new ArgumentException("A finite UTC instant is required.");
    }
}
