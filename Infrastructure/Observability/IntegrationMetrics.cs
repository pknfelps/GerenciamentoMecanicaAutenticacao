using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;

public sealed class IntegrationMetrics : IDisposable
{
    public const string MeterName = "Mecanica.Integrations";
    public const string InstrumentName = "mecanica.integrations.operations";
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> operations;

    public IntegrationMetrics() => operations = meter.CreateCounter<long>(InstrumentName, "{operation}");

    public static T Measure<T>(IntegrationMetrics? metrics, string integration, string operation, string purpose,
        Func<T> action, CancellationToken callerCancellation = default)
    {
        var outcome = "success";
        try { return action(); }
        catch (Exception error)
        {
            outcome = Outcome(error, callerCancellation);
            throw;
        }
        finally { metrics?.Record(integration, operation, purpose, outcome); }
    }

    public static async Task<T> MeasureAsync<T>(IntegrationMetrics? metrics, string integration, string operation, string purpose,
        Func<Task<T>> action, CancellationToken callerCancellation = default)
    {
        var outcome = "success";
        try { return await action(); }
        catch (Exception error)
        {
            outcome = Outcome(error, callerCancellation);
            throw;
        }
        finally { metrics?.Record(integration, operation, purpose, outcome); }
    }

    private static string Outcome(Exception error, CancellationToken callerCancellation) => error switch
    {
        OperationCanceledException => callerCancellation.IsCancellationRequested ? "canceled" : "timeout",
        TimeoutException => "timeout",
        Npgsql.NpgsqlException { InnerException: TimeoutException } => "timeout",
        _ => "failure"
    };

    private void Record(string integration, string operation, string purpose, string outcome)
    {
        if (integration is not ("postgresql" or "ssm" or "secretsmanager")
            || operation is not ("connection.open" or "command.execute" or "transaction.begin" or "transaction.commit"
                or "transaction.rollback" or "get_parameters" or "get_secret_value")
            || purpose is not ("application" or "bootstrap" or "readiness" or "telemetry"))
            return;

        try
        {
            operations.Add(1, new TagList
            {
                { "integration", integration }, { "operation", operation }, { "purpose", purpose }, { "outcome", outcome }
            });
        }
        catch (Exception)
        {
            // Measurement listeners must never replace the result or exception of the dependency.
        }
    }

    public void Dispose() => meter.Dispose();
}
