using GerenciamentoMecanica.Autenticacao.Function.Observability;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json.Nodes;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests.Observability;

public sealed class TelemetrySettingsTests
{
    private static readonly string[] ExpectedFormats = ["tracecontext"];

    [Test]
    public void VersionedManifestSelectsOnlyW3C()
    {
        using var configuration = Configuration("[\"tracecontext\"]");

        var settings = TelemetrySettings.Read(configuration);

        Assert.That(settings!.InboundFormats, Is.EqualTo(ExpectedFormats));
    }

    [TestCase(null)]
    [TestCase("[]")]
    [TestCase("[\"xray\"]")]
    [TestCase("[\"xray\",\"tracecontext\"]")]
    [TestCase("[\"tracecontext\",\"tracecontext\"]")]
    [TestCase("[\"TraceContext\"]")]
    [TestCase("[\"private-format\"]")]
    [TestCase("\"private-value\"")]
    public void RejectsUnsupportedPropagationWithAFixedMessage(string? formats)
    {
        using var configuration = Configuration(formats);

        var error = Assert.Throws<InvalidOperationException>(() => TelemetrySettings.Read(configuration));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Is.EqualTo("Configuração de telemetria inválida."));
            Assert.That(error.InnerException, Is.Null);
        });
    }

    [Test]
    public void DisabledTelemetryIgnoresInvalidPropagation()
    {
        using var configuration = Configuration("[\"private-format\"]");
        configuration["Telemetry:Enabled"] = "false";

        Assert.That(TelemetrySettings.Read(configuration), Is.Null);
    }

    private static ConfigurationRoot Configuration(string? formats)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Observability", "observability.json")))!;
        manifest["Telemetry"]!["Enabled"] = true;
        manifest["Telemetry"]!["Propagation"]!["InboundFormats"] = formats is null ? null : JsonNode.Parse(formats);
        return (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToJsonString())))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:Environment"] = "hom",
                ["AWS_REGION"] = "us-east-1",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4319"
            }).Build();
    }
}
