using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Extensions.AWS.Trace;
using System.Diagnostics;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class LambdaTracePropagator : TextMapPropagator
{
    private readonly string[] inboundFormats;
    private readonly AWSXRayPropagator xray = new();
    private readonly TraceContextPropagator w3c = new();
    private readonly HashSet<string> fields;

    public LambdaTracePropagator(IEnumerable<string> inboundFormats)
    {
        this.inboundFormats = inboundFormats.ToArray();
        fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "traceparent" };
        if (this.inboundFormats.Contains("xray"))
            fields.UnionWith(xray.Fields);
    }

    public override ISet<string> Fields => fields;

    public override PropagationContext Extract<T>(PropagationContext context, T carrier,
        Func<T, string, IEnumerable<string>?> getter)
    {
        var extracted = WithoutMetadata(context.ActivityContext);
        foreach (var format in inboundFormats)
        {
            if (format == "xray")
            {
                // The runtime changes this value per invocation; never read it from cached configuration.
                var runtime = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");
                if (!string.IsNullOrEmpty(runtime))
                    extracted = xray.Extract(extracted, runtime, static (header, _) => [header]);
                extracted = xray.Extract(extracted, carrier, getter);
            }
            else if (format == "tracecontext")
            {
                extracted = w3c.Extract(extracted, carrier,
                    (input, name) => string.Equals(name, "tracestate", StringComparison.OrdinalIgnoreCase)
                        ? []
                        : getter(input, name));
            }
            else
            {
                throw new InvalidOperationException("Configuração de telemetria inválida.");
            }
        }
        return WithoutMetadata(extracted.ActivityContext);
    }

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter) =>
        w3c.Inject(WithoutMetadata(context.ActivityContext), carrier, setter);

    private static PropagationContext WithoutMetadata(ActivityContext context) =>
        new(new ActivityContext(context.TraceId, context.SpanId, context.TraceFlags, isRemote: context.IsRemote), default);
}
