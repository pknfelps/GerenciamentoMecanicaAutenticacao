namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class TelemetryCollectionOptions
{
    public string InvocationSpanName { get; init; } = "";
    public string DatabaseSpanName { get; init; } = "";
    public string InvocationErrorMessage { get; init; } = "";
    public string DatabaseErrorMessage { get; init; } = "";
    public string ExportFailureMessage { get; init; } = "";
    public HashSet<string> InvocationAttributes { get; init; } = [];
    public HashSet<string> DatabaseAttributes { get; init; } = [];
    public HashSet<string> ExceptionAttributes { get; init; } = [];
    public string[] MetricAttributes { get; init; } = [];
    public double[] DurationBoundariesSeconds { get; init; } = [];

    public void Validate()
    {
        if (new[] { InvocationSpanName, DatabaseSpanName, InvocationErrorMessage, DatabaseErrorMessage, ExportFailureMessage }
                .Any(string.IsNullOrWhiteSpace)
            || InvocationAttributes.Count == 0 || DatabaseAttributes.Count == 0 || ExceptionAttributes.Count == 0
            || MetricAttributes.Length == 0 || MetricAttributes.Any(key => key is not ("outcome" or "http.response.status_code"))
            || DurationBoundariesSeconds.Length == 0
            || DurationBoundariesSeconds.Any(value => !double.IsFinite(value) || value < 0)
            || !DurationBoundariesSeconds.SequenceEqual(DurationBoundariesSeconds.Distinct().Order()))
            throw new InvalidOperationException("Configuração de coleta de telemetria inválida.");
    }
}
