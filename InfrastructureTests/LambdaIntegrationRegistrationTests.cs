using GerenciamentoMecanica.Autenticacao.Function.Observability;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

[NonParallelizable]
public sealed class LambdaIntegrationRegistrationTests
{
    private TextMapPropagator original = null!;

    [SetUp]
    public void SavePropagator() => original = Propagators.DefaultTextMapPropagator;

    [TearDown]
    public void RestorePropagator() => Sdk.SetDefaultTextMapPropagator(original);

    [Test]
    public void DisabledTelemetryDoesNotCreateObservabilityOrIntegrationInstruments()
    {
        var configuration = Configuration();
        configuration["Telemetry:Enabled"] = "false";
        using var listener = new IntegrationMetricListener();

        using var observability = LambdaObservability.Create(configuration);

        Assert.That(observability, Is.Null);
        Assert.That(listener.Measurements, Is.Empty);
        Assert.That(Propagators.DefaultTextMapPropagator, Is.SameAs(original));
    }

    [Test]
    public void ReusedObservabilityOwnsOneIntegrationComponent()
    {
        var configuration = Configuration();
        using var listener = new IntegrationMetricListener();
        using var observability = LambdaObservability.Create(configuration)!;
        var metrics = observability.Integrations;

        IntegrationMetrics.Measure(metrics, "ssm", "get_parameters", "bootstrap", () => true);
        IntegrationMetrics.Measure(observability.Integrations, "ssm", "get_parameters", "bootstrap", () => true);

        Assert.That(metrics, Is.Not.Null);
        Assert.That(observability.Integrations, Is.SameAs(metrics));
        Assert.That(listener.Measurements.Count, Is.EqualTo(2));
    }

    [Test]
    public void IntegrationMeterCanBeDisabledWithoutRemovingInvocationObservability()
    {
        var configuration = Configuration();
        configuration[$"Metrics:EnabledMetrics:{IntegrationMetrics.MeterName}"] = "false";

        using var observability = LambdaObservability.Create(configuration);

        Assert.That(observability, Is.Not.Null);
        Assert.That(observability!.Integrations, Is.Null);
    }

    [TestCase("private-field")]
    [TestCase("integration")]
    public void InvalidMetricDimensionsFailWithASanitizedConfigurationMessage(string attribute)
    {
        var configuration = Configuration();
        configuration["Telemetry:IntegrationMetricAttributes:3"] = attribute;

        var error = Assert.Throws<InvalidOperationException>(() => LambdaObservability.Create(configuration));

        Assert.That(error!.Message, Is.EqualTo("Configuração de telemetria inválida."));
        Assert.That(error.InnerException, Is.Null);
    }

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Observability", "observability.json"))
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Enabled"] = "true",
            ["Runtime:Environment"] = "hom",
            ["AWS_REGION"] = "us-east-1",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            ["OTEL_METRIC_EXPORT_INTERVAL"] = "3600000"
        }).Build();
}
