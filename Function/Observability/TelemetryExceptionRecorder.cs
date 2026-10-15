using System.Diagnostics;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class TelemetryExceptionRecorder : IDisposable
{
    private readonly ActivityListener listener;
    private readonly TelemetryCollectionOptions collection;

    public TelemetryExceptionRecorder(TelemetryCollectionOptions collection)
    {
        this.collection = collection;
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "OpenTelemetry.Instrumentation.AWSLambda",
            ExceptionRecorder = RecordException
        };
        ActivitySource.AddActivityListener(listener);
    }

    private void RecordException(Activity activity, Exception exception, ref TagList tags)
    {
        tags = default;
        tags.Add("exception.type", exception.GetType().FullName);
        tags.Add("exception.message", activity.Source.Name == "Npgsql"
            ? collection.DatabaseErrorMessage : collection.InvocationErrorMessage);
        // Prevent Activity.AddException from inserting Exception.ToString().
        tags.Add("exception.stacktrace", null);
    }

    public void Dispose() => listener.Dispose();
}
