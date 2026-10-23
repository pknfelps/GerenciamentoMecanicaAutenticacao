using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class InvocationMetrics
{
    public const string MeterName = "Mecanica.Auth";
    private readonly Counter<long> invocations;
    private readonly Histogram<double> duration;

    public InvocationMetrics(Meter meter)
    {

        invocations = meter.CreateCounter<long>("mecanica.auth.invocations", "{invocation}");
        duration = meter.CreateHistogram<double>("mecanica.auth.invocation.duration", "s");
    }

    public void Record(string outcome, int status, TimeSpan elapsed)
    {
        var tags = new TagList { { "outcome", outcome }, { "http.response.status_code", status } };
        invocations.Add(1, tags);
        duration.Record(elapsed.TotalSeconds, tags);
    }
}
