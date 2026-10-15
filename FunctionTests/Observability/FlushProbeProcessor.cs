using OpenTelemetry;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests.Observability;

public sealed class FlushProbeProcessor<T>(int delayMilliseconds) : BaseProcessor<T> where T : class
{
    public List<int> Timeouts { get; } = [];

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        Timeouts.Add(timeoutMilliseconds);
        Thread.Sleep(Math.Min(delayMilliseconds, timeoutMilliseconds));
        return true;
    }
}
