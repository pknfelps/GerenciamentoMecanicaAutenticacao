namespace GerenciamentoMecanica.Autenticacao.Domain.Customers;

public sealed class CustomerAccessRequestLimit
{
    public DateTime WindowStartedAt { get; private set; }
    public DateTime WindowExpiresAt { get; private set; }
    public int RequestCount { get; private set; }
    public DateTime LastRequestedAt { get; private set; }

    public CustomerAccessRequestLimit(DateTime windowStartedAt, DateTime windowExpiresAt,
        int requestCount, DateTime lastRequestedAt)
    {
        CustomerAccessChallenge.RequireUtc(windowStartedAt);
        CustomerAccessChallenge.RequireUtc(windowExpiresAt);
        CustomerAccessChallenge.RequireUtc(lastRequestedAt);
        if (windowExpiresAt - windowStartedAt != TimeSpan.FromHours(1) || requestCount is < 1 or > 5
            || lastRequestedAt < windowStartedAt || lastRequestedAt >= windowExpiresAt)
            throw new ArgumentException("Customer request limit is invalid.");
        WindowStartedAt = windowStartedAt;
        WindowExpiresAt = windowExpiresAt;
        RequestCount = requestCount;
        LastRequestedAt = lastRequestedAt;
    }

    public static CustomerAccessRequestLimit FirstRequest(DateTime now) => new(now, now.AddHours(1), 1, now);

    public bool TryAdmit(DateTime now, out int retryAfterSeconds)
    {
        CustomerAccessChallenge.RequireUtc(now);
        if (now < LastRequestedAt)
            throw new ArgumentException("Current time precedes the last admitted request.");

        var nextAllowed = LastRequestedAt.AddSeconds(60);
        if (RequestCount == 5 && WindowExpiresAt > nextAllowed)
            nextAllowed = WindowExpiresAt;
        if (now < nextAllowed)
        {
            retryAfterSeconds = (int)Math.Ceiling((nextAllowed - now).TotalSeconds);
            return false;
        }

        if (now >= WindowExpiresAt)
        {
            WindowStartedAt = now;
            WindowExpiresAt = now.AddHours(1);
            RequestCount = 1;
        }
        else
        {
            RequestCount++;
        }
        LastRequestedAt = now;
        retryAfterSeconds = 0;
        return true;
    }
}
