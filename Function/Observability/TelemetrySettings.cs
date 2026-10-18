using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class TelemetrySettings
{
    public Uri Endpoint { get; private init; } = null!;
    public string Environment { get; private init; } = "";
    public string Version { get; private init; } = "";
    public int ExportTimeoutMilliseconds { get; private init; }
    public int FlushBudgetMilliseconds { get; private init; }
    public int ResponseReserveMilliseconds { get; private init; }
    public string[] InboundFormats { get; private init; } = [];
    public TelemetryCollectionOptions Collection { get; private init; } = null!;

    public static IConfigurationRoot Configuration()
    {
        try
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("Observability/observability.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
        }
        catch (Exception)
        {
            throw Invalid();
        }
    }

    public static TelemetrySettings? Read(IConfiguration configuration)
    {
        var enabledValue = configuration["Telemetry:Enabled"];
        if (enabledValue is null || bool.TryParse(enabledValue, out var disabled) && !disabled)
            return null;
        if (!bool.TryParse(enabledValue, out var enabled) || !enabled)
            throw Invalid();

        foreach (var signal in new[] { "", "TRACES_", "METRICS_", "LOGS_" })
        {
            if (!string.IsNullOrEmpty(configuration[$"OTEL_EXPORTER_OTLP_{signal}HEADERS"])
                || signal.Length > 0 && (configuration[$"OTEL_EXPORTER_OTLP_{signal}ENDPOINT"] is not null
                    || configuration[$"OTEL_EXPORTER_OTLP_{signal}PROTOCOL"] is not null
                    || configuration[$"OTEL_EXPORTER_OTLP_{signal}TIMEOUT"] is not null))
                throw Invalid();
        }
        if (configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] is not (null or "http/protobuf")
            || !Uri.TryCreate(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0
            || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw Invalid();

        var environment = configuration["Runtime:Environment"];
        if (environment is not ("hom" or "prd") || configuration["AWS_REGION"] != "us-east-1")
            throw Invalid();

        try
        {
            var collection = configuration.GetSection("Telemetry:Collection").Get<TelemetryCollectionOptions>() ?? throw Invalid();
            collection.Validate();
            var inboundFormats = configuration.GetSection("Telemetry:Propagation:InboundFormats").Get<string[]>() ?? [];
            if (inboundFormats.Length == 0
                || inboundFormats.Any(format => format is not ("xray" or "tracecontext"))
                || inboundFormats.Distinct(StringComparer.Ordinal).Count() != inboundFormats.Length)
                throw Invalid();
            var flush = configuration.GetValue<int>("Telemetry:FlushBudgetMilliseconds");
            var reserve = configuration.GetValue<int>("Telemetry:ResponseReserveMilliseconds");
            if (flush is < 1 or > 500 || reserve is < 500 or > 1000
                || configuration.GetValue<int>("OTEL_EXPORTER_OTLP_TIMEOUT") is < 1 or > 100)
                throw Invalid();

            foreach (var timeout in new[] { "OTEL_BSP_EXPORT_TIMEOUT", "OTEL_BLRP_EXPORT_TIMEOUT", "OTEL_METRIC_EXPORT_TIMEOUT" })
                if (configuration.GetValue<int>(timeout) is < 1 or > 100)
                    throw Invalid();
            foreach (var key in new[] { "OTEL_BSP_MAX_QUEUE_SIZE", "OTEL_BSP_MAX_EXPORT_BATCH_SIZE", "OTEL_BSP_SCHEDULE_DELAY",
                         "OTEL_BLRP_MAX_QUEUE_SIZE", "OTEL_BLRP_MAX_EXPORT_BATCH_SIZE", "OTEL_BLRP_SCHEDULE_DELAY", "OTEL_METRIC_EXPORT_INTERVAL" })
                if (configuration.GetValue<int>(key) < 1)
                    throw Invalid();
            if (configuration.GetValue<int>("OTEL_BSP_MAX_EXPORT_BATCH_SIZE") > configuration.GetValue<int>("OTEL_BSP_MAX_QUEUE_SIZE")
                || configuration.GetValue<int>("OTEL_BLRP_MAX_EXPORT_BATCH_SIZE") > configuration.GetValue<int>("OTEL_BLRP_MAX_QUEUE_SIZE"))
                throw Invalid();
            _ = configuration.GetValue<bool>($"Metrics:EnabledMetrics:{InvocationMetrics.MeterName}");

            // Resource metadata is public: accept only technical identity fields.
            var resource = configuration["OTEL_RESOURCE_ATTRIBUTES"];
            if (resource is not null && resource.Split(',').Any(item =>
                    item.Split('=', 2) is not [var key, var value]
                    || key.Trim() is not ("service.name" or "service.version" or "deployment.environment.name")
                    || string.IsNullOrWhiteSpace(value)))
                throw Invalid();

            return new()
            {
                Endpoint = endpoint, Environment = environment,
                ExportTimeoutMilliseconds = configuration.GetValue<int>("OTEL_EXPORTER_OTLP_TIMEOUT"),
                Version = ReadVersion(typeof(FunctionHandler).Assembly),
                FlushBudgetMilliseconds = flush, ResponseReserveMilliseconds = reserve, Collection = collection,
                InboundFormats = inboundFormats
            };
        }
        catch (Exception)
        {
            throw Invalid();
        }
    }

    private static InvalidOperationException Invalid() => new("Configuração de telemetria inválida.");

    public static string ReadVersion(Assembly assembly)
    {
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+', 2).ElementAtOrDefault(1);
        return value is { Length: >= 40 } && value[..40].All(Uri.IsHexDigit)
            ? value[..40].ToLowerInvariant() : "local";
    }
}
