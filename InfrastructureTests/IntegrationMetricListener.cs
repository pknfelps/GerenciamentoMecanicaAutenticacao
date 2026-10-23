using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

// Captures only the native counter: no exporter, Collector or external destination.
public sealed class IntegrationMetricListener : IDisposable
{
    private readonly MeterListener listener = new();
    public ConcurrentQueue<Dictionary<string, object?>> Measurements { get; } = new();

    public IntegrationMetricListener()
    {
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == IntegrationMetrics.MeterName && instrument.Name == IntegrationMetrics.InstrumentName)
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.That(value, Is.EqualTo(1));
            Measurements.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        listener.Start();
    }

    public void Dispose() => listener.Dispose();
}
