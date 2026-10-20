using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Instrumentation.AWSLambda;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using System.Diagnostics;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class LambdaObservability : IDisposable
{
    private readonly ServiceProvider services;
    private readonly TelemetrySettings settings;
    private readonly TelemetryExceptionRecorder exceptionRecorder;
    private readonly TracerProvider traces;
    private readonly LambdaTracePropagator propagator;
    private readonly MeterProvider metrics;
    private readonly LoggerProvider logs;
    private readonly ILogger logger;
    private readonly InvocationMetrics? invocations;
    public IntegrationMetrics? Integrations { get; }
    private IDisposable? ownedConfiguration;
    private int disposed;

    private LambdaObservability(ServiceProvider services, TelemetrySettings settings)
    {
        this.services = services;
        this.settings = settings;

        traces = services.GetRequiredService<TracerProvider>();
        propagator = services.GetRequiredService<LambdaTracePropagator>();
        metrics = services.GetRequiredService<MeterProvider>();
        logs = services.GetRequiredService<LoggerProvider>();
        logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FunctionHandler).FullName!);
        invocations = services.GetService<InvocationMetrics>();
        Integrations = services.GetService<IntegrationMetrics>();
        exceptionRecorder = new TelemetryExceptionRecorder(settings.Collection);
    }

    public static LambdaObservability? CreateDefault()
    {
        var configuration = TelemetrySettings.Configuration();
        try
        {
            var telemetry = Create(configuration);
            if (telemetry is null)
                (configuration as IDisposable)?.Dispose();
            else
                telemetry.ownedConfiguration = configuration as IDisposable;
            return telemetry;
        }
        catch
        {
            (configuration as IDisposable)?.Dispose();
            throw;
        }
    }

    public static LambdaObservability? Create(IConfiguration configuration,
        Action<TracerProviderBuilder>? configureTraces = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<OpenTelemetryLoggerOptions>? configureLogs = null)
    {
        var settings = TelemetrySettings.Read(configuration);
        if (settings is null)
            return null;

        var propagator = new LambdaTracePropagator(settings.InboundFormats);
        Sdk.SetDefaultTextMapPropagator(propagator);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(propagator);

        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddOpenTelemetry(options =>
            {
                configuration.GetSection("Logging:OpenTelemetry").Bind(options);
                options.IncludeScopes = false;
                configureLogs?.Invoke(options);
            });
        });
        var integrationsEnabled = configuration.GetValue<bool>($"Metrics:EnabledMetrics:{IntegrationMetrics.MeterName}");
        if (integrationsEnabled)
            services.AddSingleton<IntegrationMetrics>();
        var meterEnabled = configuration.GetValue<bool>($"Metrics:EnabledMetrics:{InvocationMetrics.MeterName}");
        if (meterEnabled)
        {
            services.AddSingleton(_ => new System.Diagnostics.Metrics.Meter(InvocationMetrics.MeterName));
            services.AddSingleton<InvocationMetrics>();
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear()
                .AddService("mecanica-auth", serviceVersion: settings.Version, autoGenerateServiceInstanceId: false)
                .AddTelemetrySdk()
                .AddAttributes([new("deployment.environment.name", settings.Environment)])
                .AddAttributes(ReadResourceAttributes(configuration)))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler())
                    .AddAWSLambdaConfigurations(options => options.DisableAwsXRayContextExtraction = true)
                    .AddNpgsql()
                    .AddProcessor(new SafeTraceProcessor(settings.Collection));
                configureTraces?.Invoke(tracing);
            })
            .WithMetrics(metric =>
            {
                if (meterEnabled)
                    metric.AddMeter(InvocationMetrics.MeterName)
                        .AddView("mecanica.auth.invocations", new MetricStreamConfiguration
                        { TagKeys = settings.Collection.MetricAttributes })
                        .AddView("mecanica.auth.invocation.duration", new ExplicitBucketHistogramConfiguration
                        {
                            TagKeys = settings.Collection.MetricAttributes,
                            Boundaries = settings.Collection.DurationBoundariesSeconds
                        });
                if (integrationsEnabled)
                    metric.AddMeter(IntegrationMetrics.MeterName)
                        .AddView(IntegrationMetrics.InstrumentName, new MetricStreamConfiguration
                        { TagKeys = settings.IntegrationMetricAttributes });
                configureMetrics?.Invoke(metric);
            })
            .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, settings.Endpoint);

        var provider = services.BuildServiceProvider();
        try { return new LambdaObservability(provider, settings); }
        catch
        {
            provider.Dispose();
            throw new InvalidOperationException("Configuração de telemetria inválida.");
        }
    }

    private static IEnumerable<KeyValuePair<string, object>> ReadResourceAttributes(IConfiguration configuration)
    {
        var attributes = new Dictionary<string, object>();
        if (configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } name)
            attributes["service.name"] = name;
        foreach (var item in configuration["OTEL_RESOURCE_ATTRIBUTES"]?.Split(',') ?? [])
        {
            var parts = item.Split('=', 2);
            attributes[parts[0].Trim()] = parts[1].Trim();
        }
        return attributes;
    }

    public async Task<APIGatewayProxyResponse> InvokeAsync(APIGatewayProxyRequest request, ILambdaContext context,
        Func<APIGatewayProxyRequest, ILambdaContext, Task<APIGatewayProxyResponse>> handler)
    {
        var parent = propagator.Extract(default, request, static (input, key) =>
        {
            var multiValue = input.MultiValueHeaders?.Where(header => string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (multiValue is { Length: > 0 })
                return multiValue.SelectMany(header => header.Value ?? []);
            return input.Headers?.Where(header => string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value) ?? [];
        }).ActivityContext;

        // Null disables only the wrapper's implicit, unbounded ForceFlush.
        var response = await AWSLambdaWrapper.TraceAsync(null, handler, request, context, parent);
        Flush(context);
        return response;
    }

    public void Complete(ILambdaContext context, APIGatewayProxyRequest request, int status, string outcome, TimeSpan elapsed)
    {
        var activity = Activity.Current;
        activity?.SetTag("http.response.status_code", status);
        if (status >= 500)
            activity?.SetStatus(ActivityStatusCode.Error, settings.Collection.InvocationErrorMessage);
        else
            activity?.SetStatus(ActivityStatusCode.Unset);

        var fields = new Dictionary<string, object?>
        {
            ["lambdaRequestId"] = context.AwsRequestId,
            ["gatewayRequestId"] = request.RequestContext?.RequestId,
            ["status"] = status, ["durationMs"] = (long)elapsed.TotalMilliseconds, ["category"] = outcome,
            ["traceId"] = activity?.TraceId.ToString(), ["spanId"] = activity?.SpanId.ToString()
        };
        var json = JsonSerializer.Serialize(fields);
        context.Logger.LogLine(json);
        try
        {
            invocations?.Record(outcome, status, elapsed);
            logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, new EventId(1, "InvocationCompleted"),
                fields, null, static (state, _) => JsonSerializer.Serialize(state));
        }
        catch (Exception error)
        {
            Diagnostic(context, error.GetType().FullName);
        }
    }

    public void RecordException(Exception exception) => Activity.Current?.AddException(exception);

    private void Flush(ILambdaContext context)
    {
        var elapsed = Stopwatch.StartNew();
        foreach (var flush in new Func<int, bool>[] { traces.ForceFlush, metrics.ForceFlush, logs.ForceFlush })
        {
            var available = Math.Min(settings.FlushBudgetMilliseconds - elapsed.Elapsed.TotalMilliseconds,
                context.RemainingTime.TotalMilliseconds - settings.ResponseReserveMilliseconds);
            if (available < settings.ExportTimeoutMilliseconds)
                break;
            try
            {
                if (!flush((int)available))
                    Diagnostic(context);
            }
            catch (Exception error)
            {
                Diagnostic(context, error.GetType().FullName);
            }
        }
    }

    private void Diagnostic(ILambdaContext context, string? errorType = null) => context.Logger.LogLine(JsonSerializer.Serialize(new
    {
        category = "telemetry-failure", message = settings.Collection.ExportFailureMessage, errorType
    }));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try { services.Dispose(); }
        finally
        {
            exceptionRecorder.Dispose();
            ownedConfiguration?.Dispose();
        }
    }
}
