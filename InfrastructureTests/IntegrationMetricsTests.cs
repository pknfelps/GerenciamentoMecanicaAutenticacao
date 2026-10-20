using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using Npgsql;
using System.Diagnostics.Metrics;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

[NonParallelizable]
public sealed class IntegrationMetricsTests
{
    [Test]
    public void SuccessPreservesTheResultAndRecordsOneOperation()
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();

        var result = IntegrationMetrics.Measure(metrics, "postgresql", "connection.open", "application", () => 42);

        Assert.That(result, Is.EqualTo(42));
        AssertMeasurement(listener, "success");
    }

    [TestCase("failure")]
    [TestCase("timeout")]
    [TestCase("canceled")]
    [TestCase("database-timeout")]
    [TestCase("deadline")]
    public void FailurePreservesTheExceptionAndClassifiesOnlyItsType(string kind)
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        using var cancellation = new CancellationTokenSource();
        if (kind == "canceled")
            cancellation.Cancel();
        Exception expected = kind switch
        {
            "timeout" => new TimeoutException("private-value"),
            "database-timeout" => new NpgsqlException("private-value", new TimeoutException("private-inner")),
            "canceled" or "deadline" => new OperationCanceledException("private-value"),
            _ => new InvalidOperationException("private-value", new Exception("private-inner"))
        };
        expected.Data["private-data"] = "private-value";

        var actual = Assert.Catch(() => IntegrationMetrics.Measure<int>(metrics, "postgresql", "connection.open", "application",
            () => throw expected, cancellation.Token));

        Assert.That(actual, Is.SameAs(expected));
        AssertMeasurement(listener, kind is "deadline" or "database-timeout" ? "timeout" : kind);
    }

    [Test]
    public async Task AsynchronousSuccessAndFailureEachProduceOneMeasurement()
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();
        var failure = new InvalidOperationException("private-value");

        var result = await IntegrationMetrics.MeasureAsync(metrics, "ssm", "get_parameters", "bootstrap", () => Task.FromResult(9));
        var actual = Assert.ThrowsAsync<InvalidOperationException>(() => IntegrationMetrics.MeasureAsync<int>(metrics,
            "ssm", "get_parameters", "bootstrap", () => Task.FromException<int>(failure)));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(9));
            Assert.That(actual, Is.SameAs(failure));
            Assert.That(listener.Measurements.Select(point => point["outcome"]), Is.EqualTo(new[] { "success", "failure" }));
        });
    }

    [TestCase("application")]
    [TestCase("readiness")]
    [TestCase("telemetry")]
    [TestCase("bootstrap")]
    public void PurposesRemainDistinct(string purpose)
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();

        IntegrationMetrics.Measure(metrics, "postgresql", "command.execute", purpose, () => true);

        Assert.That(listener.Measurements.Single()["purpose"], Is.EqualTo(purpose));
    }

    [Test]
    public void DisabledMetricsDoNotCreateInstrumentsOrChangeTheOperation()
    {
        using var listener = new IntegrationMetricListener();

        var result = IntegrationMetrics.Measure(null, "postgresql", "connection.open", "application", () => 42);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(listener.Measurements, Is.Empty);
        });
    }

    [TestCase("private-integration", "command.execute", "application")]
    [TestCase("postgresql", "private-operation", "application")]
    [TestCase("postgresql", "command.execute", "private-purpose")]
    public void UnknownDimensionsAreNeverRecorded(string integration, string operation, string purpose)
    {
        using var listener = new IntegrationMetricListener();
        using var metrics = new IntegrationMetrics();

        IntegrationMetrics.Measure(metrics, integration, operation, purpose, () => true);

        Assert.That(listener.Measurements, Is.Empty);
    }

    [Test]
    public void AFailingListenerCannotReplaceTheDependencyExceptionOrSuccessfulResult()
    {
        using var metrics = new IntegrationMetrics();
        using var failingListener = new MeterListener();
        failingListener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == IntegrationMetrics.MeterName)
                owner.EnableMeasurementEvents(instrument);
        };
        failingListener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener-failed"));
        failingListener.Start();
        var expected = new InvalidOperationException("dependency-failed");

        var result = IntegrationMetrics.Measure(metrics, "ssm", "get_parameters", "bootstrap", () => 42);
        var actual = Assert.Throws<InvalidOperationException>(() => IntegrationMetrics.Measure<int>(metrics,
            "ssm", "get_parameters", "bootstrap", () => throw expected));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(actual, Is.SameAs(expected));
        });
    }

    private static void AssertMeasurement(IntegrationMetricListener listener, string outcome)
    {
        var measurement = listener.Measurements.Single();
        Assert.Multiple(() =>
        {
            Assert.That(measurement.Keys, Is.EquivalentTo(new[] { "integration", "operation", "purpose", "outcome" }));
            Assert.That(measurement["integration"], Is.EqualTo("postgresql"));
            Assert.That(measurement["operation"], Is.EqualTo("connection.open"));
            Assert.That(measurement["outcome"], Is.EqualTo(outcome));
            Assert.That(measurement.Values.OfType<string>(), Has.None.Contains("private"));
        });
    }
}
