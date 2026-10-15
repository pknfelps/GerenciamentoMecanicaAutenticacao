using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Function;
using GerenciamentoMecanica.Autenticacao.Function.Observability;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests.Observability;

[NonParallelizable]
public class TelemetryTests
{
    private const string Sensitive = "sensitive-document-password-SQL-token";
    private readonly TelemetryBuffer<Activity> spans = new();
    private readonly TelemetryBuffer<LogRecord> logs = new();
    private readonly TelemetryBuffer<Metric> metrics = new();
    private ILambdaContext context = null!;
    private ILambdaLogger console = null!;

    [SetUp]
    public void SetUp()
    {
        spans.Clear(); logs.Clear(); metrics.Clear();
        console = Substitute.For<ILambdaLogger>();
        context = Substitute.For<ILambdaContext>();
        context.Logger.Returns(console);
        context.AwsRequestId.Returns("lambda-id");
        context.FunctionName.Returns("mecanica-hom-auth");
        context.InvokedFunctionArn.Returns("arn:aws:lambda:us-east-1:121754142617:function:mecanica-hom-auth:1");
        context.RemainingTime.Returns(TimeSpan.FromSeconds(15));
    }

    private static APIGatewayProxyRequest Request(string? body = null) => new()
    {
        HttpMethod = "POST", Path = "/untrusted/" + Sensitive,
        Body = body ?? "{\"document\":\"52998224725\"}",
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["Authorization"] = Sensitive },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { RequestId = "gateway-id" }
    };

    private static IConfiguration Configuration(params (string Key, string? Value)[] changes)
    {
        var values = new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "true", ["Runtime:Environment"] = "hom", ["AWS_REGION"] = "us-east-1",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1"
        };
        foreach (var (key, value) in changes)
            values[key] = value;
        return new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("Observability/observability.json").AddInMemoryCollection(values).Build();
    }

    private LambdaObservability Telemetry(IConfiguration? configuration = null, BaseProcessor<Activity>? processor = null) =>
        LambdaObservability.Create(configuration ?? Configuration(),
            trace =>
            {
                if (processor is not null) trace.AddProcessor(processor);
                trace.AddInMemoryExporter(spans);
            }, metric => metric.AddInMemoryExporter(metrics),
            log => log.AddInMemoryExporter(logs))!;

    [Test]
    public void DisabledIgnoresDestinationAndCreatesNothing()
    {
        Assert.That(LambdaObservability.Create(Configuration(("Telemetry:Enabled", "false"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", Sensitive))), Is.Null);
        Assert.That(LambdaObservability.Create(new ConfigurationBuilder().Build()), Is.Null);
    }

    [TestCase("Telemetry:Enabled", "bad")]
    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", null)]
    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", "relative")]
    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", "ftp://localhost")]
    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", "http://username:password@localhost")]
    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost?secret=value")]
    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost#secret")]
    [TestCase("OTEL_EXPORTER_OTLP_HEADERS", "key=secret")]
    [TestCase("OTEL_EXPORTER_OTLP_TRACES_HEADERS", "key=secret")]
    [TestCase("OTEL_EXPORTER_OTLP_METRICS_HEADERS", "key=secret")]
    [TestCase("OTEL_EXPORTER_OTLP_LOGS_HEADERS", "key=secret")]
    [TestCase("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://other")]
    [TestCase("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://other")]
    [TestCase("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "http://other")]
    [TestCase("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "grpc")]
    [TestCase("OTEL_EXPORTER_OTLP_METRICS_PROTOCOL", "grpc")]
    [TestCase("OTEL_EXPORTER_OTLP_LOGS_PROTOCOL", "grpc")]
    [TestCase("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")]
    [TestCase("OTEL_EXPORTER_OTLP_TRACES_TIMEOUT", "1000")]
    [TestCase("OTEL_EXPORTER_OTLP_METRICS_TIMEOUT", "1000")]
    [TestCase("OTEL_EXPORTER_OTLP_LOGS_TIMEOUT", "1000")]
    [TestCase("OTEL_BSP_EXPORT_TIMEOUT", "1000")]
    [TestCase("OTEL_BLRP_EXPORT_TIMEOUT", "1000")]
    [TestCase("OTEL_METRIC_EXPORT_TIMEOUT", "1000")]
    [TestCase("Metrics:EnabledMetrics:Mecanica.Auth", "bad")]
    [TestCase("OTEL_BSP_MAX_QUEUE_SIZE", "bad")]
    [TestCase("Runtime:Environment", "dev")]
    [TestCase("Runtime:Environment", null)]
    [TestCase("AWS_REGION", "us-west-2")]
    [TestCase("Telemetry:FlushBudgetMilliseconds", "501")]
    [TestCase("Telemetry:ResponseReserveMilliseconds", "499")]
    [TestCase("OTEL_EXPORTER_OTLP_TIMEOUT", "101")]
    [TestCase("Telemetry:Collection:MetricAttributes:0", "customer.id")]
    [TestCase("OTEL_RESOURCE_ATTRIBUTES", "customer.name=secret")]
    public void InvalidConfigurationFailsWithFixedMessage(string key, string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => { using var telemetry = Telemetry(Configuration((key, value))); });
        Assert.That(error!.Message, Is.EqualTo("Configuração de telemetria inválida."));
        Assert.That(error.InnerException, Is.Null);
    }

    [TestCase(200, "authenticated")]
    [TestCase(400, "invalid-document")]
    [TestCase(401, "not-eligible")]
    [TestCase(500, "technical-failure")]
    public async Task CompletedInvocationExportsAllSignalsAndPreservesContract(int status, string outcome)
    {
        using var telemetry = Telemetry();
        using var handler = new FunctionHandler((_, _) => status switch
        {
            200 => Task.FromResult(CustomerAuthenticationResult.Authenticated(Sensitive)),
            400 => Task.FromResult(CustomerAuthenticationResult.InvalidDocument()),
            401 => Task.FromResult(CustomerAuthenticationResult.NotEligible()),
            _ => FailAsync()
        }, telemetry);
        var response = await handler.HandleAsync(Request(), context);
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(response.Headers["Cache-Control"], Is.EqualTo("no-store"));
        Assert.That(response.IsBase64Encoded, Is.False);
        var span = spans.Single();
        Assert.That(span.DisplayName, Is.EqualTo("POST /customers/validate"));
        Assert.That(span.Status, Is.EqualTo(status == 500 ? ActivityStatusCode.Error : ActivityStatusCode.Unset));
        Assert.That(logs.Count, Is.EqualTo(1));
        Assert.That(logs.Single().TraceId, Is.EqualTo(span.TraceId));
        Assert.That(logs.Single().SpanId, Is.EqualTo(span.SpanId));
        Assert.That(logs.Single().Exception, Is.Null);
        Assert.That(logs.Single().Attributes!.Any(tag => tag.Value?.ToString()?.Contains(Sensitive) == true), Is.False);
        Assert.That(logs.Single().FormattedMessage, Does.Not.Contain(Sensitive));
        Assert.That(spans.SelectMany(s => s.TagObjects).Any(t => t.Value?.ToString()?.Contains(Sensitive) == true), Is.False);
        Assert.That(span.Events.SelectMany(e => e.Tags).Any(t => t.Value?.ToString()?.Contains(Sensitive) == true), Is.False);
        Assert.That(span.Events.SelectMany(e => e.Tags).Any(t => t.Key.Contains("stacktrace")), Is.False);
        if (status == 500)
        {
            Assert.That(span.StatusDescription, Is.EqualTo("Falha durante a autenticação."));
            Assert.That(span.Events.Single().Tags.Single(t => t.Key == "exception.type").Value,
                Is.EqualTo(typeof(InvalidOperationException).FullName));
        }
        var counter = metrics.Single(m => m.Name == "mecanica.auth.invocations");
        foreach (ref readonly var point in counter.GetMetricPoints())
        {
            Assert.That(point.GetSumLong(), Is.EqualTo(1));
            var tags = new List<KeyValuePair<string, object?>>();
            foreach (var tag in point.Tags) tags.Add(tag);
            Assert.That(tags.Select(t => t.Key), Is.EquivalentTo(new[] { "outcome", "http.response.status_code" }));
            Assert.That(tags.Single(t => t.Key == "outcome").Value, Is.EqualTo(outcome));
        }
        Assert.That(metrics.Any(m => m.Name == "mecanica.auth.invocation.duration"), Is.True);
        var rendered = (string)console.ReceivedCalls().First().GetArguments()[0]!;
        Assert.That(rendered, Does.Not.Contain(Sensitive));
        using var json = JsonDocument.Parse(rendered);
        Assert.That(json.RootElement.GetProperty("traceId").GetString(), Is.EqualTo(span.TraceId.ToString()));
        Assert.That(logs.Single().FormattedMessage, Is.EqualTo(rendered));
    }

    private static async Task<CustomerAuthenticationResult> FailAsync()
    {
        await Task.Yield();
        var error = new InvalidOperationException(Sensitive, new Exception(Sensitive));
        error.Data["secret"] = Sensitive;
        throw error;
    }

    [Test]
    public async Task InvalidBodyIsObservedWithoutExecutingAuthentication()
    {
        using var telemetry = Telemetry();
        var calls = 0;
        using var handler = new FunctionHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(CustomerAuthenticationResult.NotEligible());
        }, telemetry);
        Assert.That((await handler.HandleAsync(Request("{"), context)).StatusCode, Is.EqualTo(400));
        Assert.That(calls, Is.Zero);
        Assert.That(spans.Single().Status, Is.EqualTo(ActivityStatusCode.Unset));
        Assert.That(logs.Single().FormattedMessage, Does.Contain("invalid-request"));
    }

    [TestCase("00")]
    [TestCase("01")]
    public async Task W3CParentIsPreservedAndAlwaysSampledWithoutBaggage(string flags)
    {
        using var telemetry = Telemetry();
        using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
        var request = Request();
        request.Headers["TraceParent"] = "00-11111111111111111111111111111111-2222222222222222-" + flags;
        request.Headers["tracestate"] = Sensitive;
        request.Headers["baggage"] = "customer=" + Sensitive;
        await handler.HandleAsync(request, context);
        var span = spans.Single();
        Assert.That(span.TraceId.ToString(), Is.EqualTo("11111111111111111111111111111111"));
        Assert.That(span.ParentSpanId.ToString(), Is.EqualTo("2222222222222222"));
        Assert.That(span.Recorded, Is.True);
        Assert.That(span.Baggage, Is.Empty);
        Assert.That(span.TraceStateString, Is.Null);
    }

    [Test]
    public async Task ConcurrentAndRepeatedInvocationsKeepSeparateContextsAndDoNotMultiplyLogs()
    {
        using var telemetry = Telemetry();
        using var handler = new FunctionHandler(async (_, token) =>
        {
            await Task.Delay(20, token);
            return CustomerAuthenticationResult.NotEligible();
        }, telemetry);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => handler.HandleAsync(Request(), context)));
        await handler.HandleAsync(Request(), context);
        Assert.That(spans.Count, Is.EqualTo(4));
        Assert.That(logs.Count, Is.EqualTo(4));
        Assert.That(spans.Select(s => s.TraceId).Distinct().Count(), Is.EqualTo(4));
        Assert.That(logs.Select(l => l.TraceId), Is.EquivalentTo(spans.Select(s => s.TraceId)));
        Assert.That(Activity.Current, Is.Null);
    }

    [Test]
    public async Task TimeoutIsRecordedWithoutReturningTokenOrOriginalException()
    {
        context.RemainingTime.Returns(TimeSpan.FromMilliseconds(1050));
        using var telemetry = Telemetry();
        using var handler = new FunctionHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return CustomerAuthenticationResult.Authenticated(Sensitive);
        }, telemetry);
        Assert.That((await handler.HandleAsync(Request(), context)).StatusCode, Is.EqualTo(500));
        Assert.That(logs.Single().FormattedMessage, Does.Contain("timeout").And.Not.Contain(Sensitive));
        Assert.That(spans.Single().Status, Is.EqualTo(ActivityStatusCode.Error));
    }

    [Test]
    public async Task LowRemainingTimeSkipsFlushAndPreservesResponse()
    {
        var processor = new FlushProbeProcessor<Activity>(0);
        context.RemainingTime.Returns(TimeSpan.FromMilliseconds(400));
        using var telemetry = Telemetry(processor: processor);
        using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
        Assert.That((await handler.HandleAsync(Request("{"), context)).StatusCode, Is.EqualTo(400));
        Assert.That(processor.Timeouts, Is.Empty);
        Assert.That(spans.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task SlowProcessorUsesOnlyGlobalBudgetAndDoesNotChangeResponse()
    {
        var processor = new FlushProbeProcessor<Activity>(250);
        using var telemetry = Telemetry(Configuration(("Telemetry:FlushBudgetMilliseconds", "300")), processor);
        using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
        var elapsed = Stopwatch.StartNew();
        Assert.That((await handler.HandleAsync(Request(), context)).StatusCode, Is.EqualTo(401));
        Assert.That(processor.Timeouts.Single(), Is.InRange(1, 300));
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task DisabledMeterCreatesNoMeasurementsButKeepsTracesAndLogs()
    {
        using var telemetry = Telemetry(Configuration(("Metrics:EnabledMetrics:Mecanica.Auth", "false")));
        using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
        await handler.HandleAsync(Request(), context);
        Assert.That(metrics, Is.Empty);
        Assert.That(spans.Count, Is.EqualTo(1));
        Assert.That(logs.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task DisposedInstrumentationDoesNotContinueListening()
    {
        var telemetry = Telemetry();
        using (var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry))
            await handler.HandleAsync(Request(), context);
        spans.Clear();
        using var source = new ActivitySource("Npgsql");
        using var span = source.StartActivity("must-not-export");
        Assert.That(span, Is.Null);
        telemetry.Dispose();
    }

    [TestCase("hom")]
    [TestCase("prd")]
    public void TechnicalResourcesAndNativeExportOptionsApplyToAllSignals(string environment)
    {
        IServiceProvider? services = null;
        using var telemetry = LambdaObservability.Create(Configuration(
            ("Runtime:Environment", environment), ("OTEL_SERVICE_NAME", "auth-configured"),
            ("OTEL_RESOURCE_ATTRIBUTES", "service.version=revision-test,deployment.environment.name=" + environment),
            ("OTEL_BSP_MAX_QUEUE_SIZE", "1024"), ("OTEL_BSP_MAX_EXPORT_BATCH_SIZE", "256"),
            ("OTEL_BLRP_MAX_QUEUE_SIZE", "512"), ("OTEL_BLRP_MAX_EXPORT_BATCH_SIZE", "128"),
            ("OTEL_METRIC_EXPORT_INTERVAL", "45000")),
            trace => trace.AddProcessor(provider =>
            {
                services = provider;
                return new FlushProbeProcessor<Activity>(0);
            }));
        Assert.That(services, Is.Not.Null);
        foreach (var provider in new BaseProvider[] { services!.GetRequiredService<TracerProvider>(),
                     services!.GetRequiredService<MeterProvider>(), services!.GetRequiredService<LoggerProvider>() })
        {
            var attributes = provider.GetResource().Attributes.ToDictionary(item => item.Key, item => item.Value);
            Assert.That(attributes["service.name"], Is.EqualTo("auth-configured"));
            Assert.That(attributes["service.version"], Is.EqualTo("revision-test"));
            Assert.That(attributes["deployment.environment.name"], Is.EqualTo(environment));
            Assert.That(attributes.ContainsKey("service.instance.id"), Is.False);
        }
        var exporter = services!.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().CurrentValue;
        var traces = services!.GetRequiredService<IOptionsMonitor<ActivityExportProcessorOptions>>().CurrentValue.BatchExportProcessorOptions;
        var logOptions = services!.GetRequiredService<IOptionsMonitor<LogRecordExportProcessorOptions>>().CurrentValue.BatchExportProcessorOptions;
        var metric = services!.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().CurrentValue;
        Assert.That(exporter.Protocol, Is.EqualTo(OtlpExportProtocol.HttpProtobuf));
        Assert.That(exporter.TimeoutMilliseconds, Is.EqualTo(100));
        Assert.That(traces.MaxQueueSize, Is.EqualTo(1024));
        Assert.That(traces.MaxExportBatchSize, Is.EqualTo(256));
        Assert.That(traces.ExporterTimeoutMilliseconds, Is.EqualTo(100));
        Assert.That(logOptions.MaxQueueSize, Is.EqualTo(512));
        Assert.That(logOptions.MaxExportBatchSize, Is.EqualTo(128));
        Assert.That(logOptions.ExporterTimeoutMilliseconds, Is.EqualTo(100));
        Assert.That(metric.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds, Is.EqualTo(45000));
        Assert.That(metric.TemporalityPreference, Is.EqualTo(MetricReaderTemporalityPreference.Delta));
    }

    [Test]
    public async Task DeclarativeAttributesMessagesHistogramAndLogFiltersAreApplied()
    {
        using var telemetry = Telemetry(Configuration(
            ("Telemetry:Collection:InvocationSpanName", "authentication"),
            ("Telemetry:Collection:InvocationErrorMessage", "Falha técnica configurada."),
            ("Telemetry:Collection:InvocationAttributes:0", "http.route"),
            ("Telemetry:Collection:DurationBoundariesSeconds:0", "0.001"),
            ("Logging:OpenTelemetry:LogLevel:GerenciamentoMecanica.Autenticacao.Function", "None")));
        using var handler = new FunctionHandler((_, _) => FailAsync(), telemetry);
        Assert.That((await handler.HandleAsync(Request(), context)).StatusCode, Is.EqualTo(500));
        Assert.That(spans.Single().DisplayName, Is.EqualTo("authentication"));
        Assert.That(spans.Single().StatusDescription, Is.EqualTo("Falha técnica configurada."));
        Assert.That(logs, Is.Empty);
        var histogram = metrics.Single(metric => metric.Name == "mecanica.auth.invocation.duration");
        foreach (ref readonly var point in histogram.GetMetricPoints())
        {
            var buckets = new List<double>();
            foreach (var bucket in point.GetHistogramBuckets()) buckets.Add(bucket.ExplicitBound);
            Assert.That(buckets, Does.Contain(0.001));
            Assert.That(point.GetHistogramCount(), Is.EqualTo(1));
            Assert.That(point.GetHistogramSum(), Is.GreaterThanOrEqualTo(0));
        }
    }

    [Test]
    public async Task SlowOtlpEndpointIsBoundedAndCannotReplaceSuccessfulResponse()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var accepted = 0;
        async Task ServeAsync()
        {
            var connections = new List<System.Net.Sockets.TcpClient>();
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    connections.Add(await listener.AcceptTcpClientAsync(cancellation.Token));
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (OperationCanceledException) { }
            finally { foreach (var connection in connections) connection.Dispose(); }
        }
        var serving = ServeAsync();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var telemetry = Telemetry(Configuration(("OTEL_EXPORTER_OTLP_ENDPOINT", $"http://127.0.0.1:{port}")));
            using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
            var elapsed = Stopwatch.StartNew();
            Assert.That((await handler.HandleAsync(Request(), context)).StatusCode, Is.EqualTo(401));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
            Assert.That(accepted, Is.GreaterThan(0));
            Assert.That(spans.Count, Is.EqualTo(1));
        }
        finally
        {
            cancellation.Cancel();
            await serving;
        }
    }

    [Test]
    public async Task MissingW3CContextCreatesRootAndDoesNotExtractXRay()
    {
        const string variable = "_X_AMZN_TRACE_ID";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "Root=1-11111111-111111111111111111111111;Parent=2222222222222222;Sampled=1");
            using var telemetry = Telemetry();
            using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
            var request = Request();
            request.Headers["x-amzn-trace-id"] = "Root=1-11111111-111111111111111111111111;Parent=2222222222222222;Sampled=1";
            await handler.HandleAsync(request, context);
            Assert.That(spans.Single().ParentSpanId, Is.EqualTo(default(ActivitySpanId)));
            Assert.That(spans.Single().TraceId, Is.Not.EqualTo(default(ActivityTraceId)));
            Assert.That(spans.Single().TraceId.ToString(), Is.Not.EqualTo(new string('1', 32)));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Test]
    public async Task ThreeSignalsShareDecreasingFlushTimeoutsAndKeepSerializationReserve()
    {
        var trace = new FlushProbeProcessor<Activity>(20);
        var metric = new FlushProbeMetricReader();
        var log = new FlushProbeProcessor<LogRecord>(10);
        using var telemetry = LambdaObservability.Create(Configuration(),
            tracing => tracing.AddProcessor(trace),
            metering => metering.AddReader(metric),
            logging => logging.AddProcessor(log));
        using var handler = new FunctionHandler((_, _) => Task.FromResult(CustomerAuthenticationResult.NotEligible()), telemetry);
        var remaining = Stopwatch.StartNew();
        context.RemainingTime.Returns(_ => TimeSpan.FromMilliseconds(1500) - remaining.Elapsed);
        Assert.That((await handler.HandleAsync(Request(), context)).StatusCode, Is.EqualTo(401));
        Assert.That(trace.Timeouts.Count, Is.EqualTo(1));
        Assert.That(metric.Timeouts.Count, Is.EqualTo(1));
        Assert.That(log.Timeouts.Count, Is.EqualTo(1));
        Assert.That(trace.Timeouts[0], Is.InRange(100, 500));
        Assert.That(metric.Timeouts[0], Is.LessThan(trace.Timeouts[0]));
        Assert.That(log.Timeouts[0], Is.LessThan(metric.Timeouts[0]));
        Assert.That(context.RemainingTime, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500)));
    }


    [Test]
    public async Task DefaultConstructorReadsDisabledJsonWithoutInitializingAwsOnInvalidBody()
    {
        var previous = Environment.GetEnvironmentVariable("Telemetry__Enabled");
        try
        {
            Environment.SetEnvironmentVariable("Telemetry__Enabled", "false");
            using var handler = new FunctionHandler();
            Assert.That((await handler.HandleAsync(Request("{"), context)).StatusCode, Is.EqualTo(400));
            using var source = new ActivitySource("Npgsql");
            using var activity = source.StartActivity("must-not-export");
            Assert.That(activity, Is.Null);
            using var json = JsonDocument.Parse((string)console.ReceivedCalls().Single().GetArguments()[0]!);
            Assert.That(json.RootElement.EnumerateObject().Count(), Is.EqualTo(5));
        }
        finally { Environment.SetEnvironmentVariable("Telemetry__Enabled", previous); }
    }

    [Test]
    public void InvalidJsonConfigurationHasOnlyFixedFailureWithoutInternalValues()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Observability", "observability.json");
        var original = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, "{\"invalid\":\"" + Sensitive);
            var error = Assert.Throws<InvalidOperationException>(() => TelemetrySettings.Configuration());
            Assert.That(error!.Message, Is.EqualTo("Configuração de telemetria inválida."));
            Assert.That(error.InnerException, Is.Null);
        }
        finally { File.WriteAllText(path, original); }
    }

}
