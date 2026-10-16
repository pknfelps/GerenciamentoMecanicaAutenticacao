using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

// Destino OTLP real de teste. Nao consulta AWS/New Relic e utiliza somente chave ficticia.
internal sealed class OtlpTestEndpoint : IAsyncDisposable
{
    public const string License = "local-test-license-not-a-real-credential";
    private readonly WebApplication application;
    private readonly ConcurrentQueue<(string Path, string? ApiKey, byte[] Body, int Status)> requests = new();
    private int statusCode = 200;
    private long delayTicks;
    private bool stopped;

    private OtlpTestEndpoint(WebApplication application) => this.application = application;

    public string CollectorEndpoint { get; private set; } = "";
    public int StatusCode { get => Volatile.Read(ref statusCode); set => Volatile.Write(ref statusCode, value); }
    public TimeSpan Delay { get => TimeSpan.FromTicks(Interlocked.Read(ref delayTicks)); set => Interlocked.Exchange(ref delayTicks, value.Ticks); }
    public (string Path, string? ApiKey, byte[] Body, int Status)[] Requests => requests.ToArray();
    public string PayloadText => string.Join("\n", Requests.Select(item => Encoding.UTF8.GetString(item.Body)));
    public bool HasAllSignals => new[] { "/v1/traces", "/v1/metrics", "/v1/logs" }
        .All(path => Requests.Any(item => item.Path == path && item.Status == 200));

    public static async Task<OtlpTestEndpoint> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        var application = builder.Build();
        var endpoint = new OtlpTestEndpoint(application);
        application.Run(async context =>
        {
            await using var body = new MemoryStream();
            if (context.Request.Headers.ContentEncoding.ToString() == "gzip")
            {
                await using var gzip = new GZipStream(context.Request.Body, CompressionMode.Decompress, leaveOpen: true);
                await gzip.CopyToAsync(body, context.RequestAborted);
            }
            else await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var status = Volatile.Read(ref endpoint.statusCode);
            endpoint.requests.Enqueue((context.Request.Path.Value!, context.Request.Headers["api-key"].ToString(), body.ToArray(), status));
            var delay = TimeSpan.FromTicks(Interlocked.Read(ref endpoint.delayTicks));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, context.RequestAborted);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/x-protobuf";
        });
        try
        {
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var port = checked((ushort)new Uri(address).Port);
            // Expor antes de construir o Collector para configurar seu acesso ao host em Windows/Linux.
            await TestcontainersSettings.ExposeHostPortsAsync(port);
            endpoint.CollectorEndpoint = $"http://host.testcontainers.internal:{port}";
            return endpoint;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public string DescribeReceivedSignals()
    {
        var captured = Requests;
        return string.Join(", ", new[] { "/v1/traces", "/v1/metrics", "/v1/logs" }.Select(path =>
            $"{path}: {captured.Count(item => item.Path == path)} requests, "
            + $"{captured.Count(item => item.Path == path && item.Status == 200)} HTTP 200"));
    }

    public async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        await application.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await application.DisposeAsync();
    }
}
