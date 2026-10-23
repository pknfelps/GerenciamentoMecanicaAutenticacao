using Amazon.Lambda.APIGatewayEvents;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class LambdaTracePropagator : TextMapPropagator
{
    private readonly TraceContextPropagator w3c = new();

    public override ISet<string> Fields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "traceparent" };

    public PropagationContext Extract(APIGatewayProxyRequest request) => Extract(default, request, static (input, key) =>
    {
        var multiValue = input.MultiValueHeaders?.Where(header => string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (multiValue is { Length: > 0 })
            return multiValue.SelectMany(header => header.Value ?? []);
        return input.Headers?.Where(header => string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value) ?? [];
    });

    public override PropagationContext Extract<T>(PropagationContext context, T carrier,
        Func<T, string, IEnumerable<string>?> getter)
    {
        var extracted = w3c.Extract(WithoutMetadata(context.ActivityContext), carrier,
            (input, name) => string.Equals(name, "tracestate", StringComparison.OrdinalIgnoreCase)
                ? []
                : getter(input, name));
        return WithoutMetadata(extracted.ActivityContext);
    }

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter) =>
        w3c.Inject(WithoutMetadata(context.ActivityContext), carrier, setter);

    private static PropagationContext WithoutMetadata(ActivityContext context) =>
        new(new ActivityContext(context.TraceId, context.SpanId, context.TraceFlags, isRemote: context.IsRemote), default);
}
