using OpenTelemetry;
using System.Diagnostics;

namespace GerenciamentoMecanica.Autenticacao.Function.Observability;

public sealed class SafeTraceProcessor(TelemetryCollectionOptions collection) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        var database = activity.Source.Name == "Npgsql";
        var allowed = database ? collection.DatabaseAttributes : collection.InvocationAttributes;
        foreach (var tag in activity.TagObjects.Where(tag => !allowed.Contains(tag.Key)).ToArray())
            activity.SetTag(tag.Key, null);
        foreach (var item in activity.Baggage.ToArray())
            activity.SetBaggage(item.Key, null);
        activity.TraceStateString = null;

        foreach (ref var item in activity.EnumerateEvents())
        {
            var eventName = item.Name;
            var tags = new ActivityTagsCollection(item.Tags.Where(tag =>
                eventName == "exception" && tag.Value is not null && collection.ExceptionAttributes.Contains(tag.Key)));
            if (tags.ContainsKey("exception.message"))
                tags["exception.message"] = database ? collection.DatabaseErrorMessage : collection.InvocationErrorMessage;
            item = new ActivityEvent("exception", item.Timestamp, tags);
        }

        activity.DisplayName = database ? collection.DatabaseSpanName : collection.InvocationSpanName;
        if (!database)
        {
            activity.SetTag("http.request.method", "POST");
            activity.SetTag("http.route", "/customers/validate");
        }
        if (activity.Status == ActivityStatusCode.Error)
            activity.SetStatus(ActivityStatusCode.Error, database
                ? collection.DatabaseErrorMessage : collection.InvocationErrorMessage);
    }
}
