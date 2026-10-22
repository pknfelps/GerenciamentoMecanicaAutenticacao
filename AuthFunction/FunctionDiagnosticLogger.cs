using Amazon.Lambda.Core;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function;

// Bridges the store's fixed diagnostic messages to Lambda and the existing application log provider.
internal sealed class FunctionDiagnosticLogger<T>(ILogger<T>? telemetry = null) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        try
        {
            var message = formatter(state, null);
            LambdaLogger.Log(JsonSerializer.Serialize(new { message, traceId = Activity.Current?.TraceId.ToString(), spanId = Activity.Current?.SpanId.ToString() }) + "\n");
            telemetry?.Log(logLevel, eventId, state, null, formatter);
        }
        catch { /* A diagnostic must not change the already confirmed operation. */ }
    }
}
