using OpenTelemetry.Metrics;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests.Observability;

public sealed class FlushProbeMetricReader : MetricReader
{
    public List<int> Timeouts { get; } = [];

    protected override bool OnCollect(int timeoutMilliseconds)
    {
        Timeouts.Add(timeoutMilliseconds);
        Thread.Sleep(Math.Min(10, timeoutMilliseconds));
        return true;
    }
}
