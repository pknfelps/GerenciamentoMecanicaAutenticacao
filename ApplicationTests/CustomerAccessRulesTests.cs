using GerenciamentoMecanica.Autenticacao.Domain.Customers;

namespace GerenciamentoMecanica.Autenticacao.ApplicationTests;

public class CustomerAccessRulesTests
{
    private static readonly DateTime Start = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    [TestCase(59, false, 1)]
    [TestCase(60, true, 0)]
    public void CooldownAllowsAnotherRequestOnlyAfterSixtySeconds(int seconds, bool allowed, int retry)
    {
        var limit = CustomerAccessRequestLimit.FirstRequest(Start);

        var admitted = limit.TryAdmit(Start.AddSeconds(seconds), out var retryAfter);

        Assert.That(admitted, Is.EqualTo(allowed));
        Assert.That(retryAfter, Is.EqualTo(retry));
        Assert.That(limit.RequestCount, Is.EqualTo(allowed ? 2 : 1));
        Assert.That(limit.LastRequestedAt, Is.EqualTo(allowed ? Start.AddSeconds(seconds) : Start));
    }

    [Test]
    public void FiveRequestsBlockTheNextUntilTheHourEnds()
    {
        var limit = new CustomerAccessRequestLimit(Start, Start.AddHours(1), 5, Start.AddMinutes(4));

        Assert.That(limit.TryAdmit(Start.AddMinutes(5), out var retry), Is.False);
        Assert.That(retry, Is.EqualTo(55 * 60));
        Assert.That(limit.RequestCount, Is.EqualTo(5));
    }

    [Test]
    public void CooldownStillAppliesWhenTheOldHourHasEnded()
    {
        var limit = new CustomerAccessRequestLimit(Start, Start.AddHours(1), 5, Start.AddMinutes(59).AddSeconds(30));

        Assert.That(limit.TryAdmit(Start.AddHours(1), out var retry), Is.False);
        Assert.That(retry, Is.EqualTo(30));
        Assert.That(limit.TryAdmit(Start.AddHours(1).AddSeconds(30), out _), Is.True);
        Assert.That(limit.RequestCount, Is.EqualTo(1));
        Assert.That(limit.WindowStartedAt, Is.EqualTo(Start.AddHours(1).AddSeconds(30)));
    }

    [Test]
    public void APartialSecondOfCooldownRoundsUp()
    {
        var limit = CustomerAccessRequestLimit.FirstRequest(Start);
        Assert.That(limit.TryAdmit(Start.AddMilliseconds(59_500), out var retry), Is.False);
        Assert.That(retry, Is.EqualTo(1));
    }

    [Test]
    public void PendingCannotBeConsumedUntilActivated()
    {
        var challenge = Pending();
        Assert.That(challenge.CanVerify(Start), Is.False);
        Assert.Throws<InvalidOperationException>(() => challenge.Consume(Start));
        Assert.That(challenge.Activate(Start), Is.True);
        challenge.Consume(Start);
        Assert.That(challenge.State, Is.EqualTo(CustomerAccessChallengeState.Consumed));
        Assert.That(challenge.Activate(Start.AddSeconds(1)), Is.False);
        Assert.That(challenge.Invalidate(Start.AddSeconds(1)), Is.False);
    }

    [Test]
    public void FifthWrongAttemptInvalidatesTheChallenge()
    {
        var challenge = Pending();
        challenge.Activate(Start);
        for (var attempt = 1; attempt <= 5; attempt++)
            challenge.RejectAttempt(Start.AddSeconds(attempt));
        Assert.That(challenge.FailedAttempts, Is.EqualTo(5));
        Assert.That(challenge.State, Is.EqualTo(CustomerAccessChallengeState.Invalidated));
        Assert.That(challenge.InvalidatedAt, Is.EqualTo(Start.AddSeconds(5)));
        Assert.That(challenge.Activate(Start.AddSeconds(6)), Is.False);
    }

    [Test]
    public void ExpirationUsesAnExclusiveEnd()
    {
        var challenge = Pending();
        Assert.That(challenge.Activate(Start.AddMinutes(5)), Is.False);
        challenge = Pending();
        challenge.Activate(Start);
        Assert.That(challenge.CanVerify(Start.AddMinutes(5).AddTicks(-1)), Is.True);
        Assert.That(challenge.CanVerify(Start.AddMinutes(5)), Is.False);
    }

    [TestCase(0)]
    [TestCase(6)]
    public void InvalidStoredRequestCountIsRejectedByCsharp(int count)
    {
        Assert.Throws<ArgumentException>(() => new CustomerAccessRequestLimit(Start, Start.AddHours(1), count, Start));
    }

    [Test]
    public void InvalidStoredVerifierSizeIsRejectedByCsharp()
    {
        Assert.Throws<ArgumentException>(() => new CustomerAccessChallenge(Guid.NewGuid(), Guid.NewGuid(),
            new byte[1], new byte[32], Start, Start.AddMinutes(5), CustomerAccessChallengeState.Pending));
    }

    private static CustomerAccessChallenge Pending() => new(Guid.NewGuid(), Guid.NewGuid(), new byte[16],
        new byte[32], Start, Start.AddMinutes(5), CustomerAccessChallengeState.Pending);
}
