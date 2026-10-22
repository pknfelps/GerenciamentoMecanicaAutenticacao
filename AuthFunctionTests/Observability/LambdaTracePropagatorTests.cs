using Amazon.Lambda.APIGatewayEvents;
using GerenciamentoMecanica.Autenticacao.Function.Observability;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests.Observability;

[NonParallelizable]
public sealed class LambdaTracePropagatorTests
{
    private const string XRayHeader = "Root=1-6ad3c000-000000000000000000000001;Parent=0000000000000101;Sampled=1";
    private const string W3CHeader = "00-00000000000000000000000000000002-0000000000000202-01";
    private const string W3CTraceId = "00000000000000000000000000000002";
    private const string W3CParentId = "0000000000000202";
    private static readonly string[] ExpectedFields = ["traceparent"];

    [TestCase(null)]
    [TestCase("invalid-w3c")]
    public void XRayRuntimeAndEventDoNotSelectAParentWithoutValidW3C(string? traceParent)
    {
        var original = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");
        try
        {
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", XRayHeader);
            var request = Request(traceParent);
            request.Headers["X-Amzn-Trace-Id"] = XRayHeader;

            var context = new LambdaTracePropagator().Extract(request);

            Assert.That(context.ActivityContext, Is.EqualTo(default(ActivityContext)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", original);
        }
    }

    [TestCase(null)]
    [TestCase(XRayHeader)]
    [TestCase("invalid-xray")]
    public void W3CParentIsIndependentOfTheXRayRuntimeAndEvent(string? runtime)
    {
        var original = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");
        try
        {
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", runtime);
            var request = Request(W3CHeader);
            request.Headers["X-Amzn-Trace-Id"] = XRayHeader;

            var context = new LambdaTracePropagator().Extract(request);

            AssertParent(context, W3CTraceId, W3CParentId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_X_AMZN_TRACE_ID", original);
        }
    }

    [Test]
    public void HeaderNamesAreCaseInsensitive()
    {
        var request = new APIGatewayProxyRequest
        {
            Headers = new Dictionary<string, string> { ["TraceParent"] = W3CHeader }
        };

        var context = new LambdaTracePropagator().Extract(request);

        AssertParent(context, W3CTraceId, W3CParentId);
    }

    [Test]
    public void MultiValueHeaderTakesPrecedenceWithoutCombiningRepresentations()
    {
        var request = Request("invalid-single-value");
        request.MultiValueHeaders = new Dictionary<string, IList<string>> { ["TraceParent"] = new[] { W3CHeader } };

        var context = new LambdaTracePropagator().Extract(request);

        AssertParent(context, W3CTraceId, W3CParentId);
    }

    [TestCase(0)]
    [TestCase(2)]
    public void EmptyOrAmbiguousMultiValueHeaderDoesNotFallBackToSingleValue(int count)
    {
        var request = Request(W3CHeader);
        request.MultiValueHeaders = new Dictionary<string, IList<string>>
        {
            ["traceparent"] = Enumerable.Repeat(W3CHeader, count).ToArray()
        };

        var context = new LambdaTracePropagator().Extract(request);

        Assert.That(context.ActivityContext, Is.EqualTo(default(ActivityContext)));
    }

    [Test]
    public void UnsampledW3CParentIsPreserved()
    {
        var context = new LambdaTracePropagator().Extract(
            Request("00-00000000000000000000000000000002-0000000000000202-00"));

        AssertParent(context, W3CTraceId, W3CParentId);
        Assert.That(context.ActivityContext.TraceFlags, Is.EqualTo(ActivityTraceFlags.None));
    }

    [Test]
    public void DoesNotReadOrReturnBaggageOrTraceState()
    {
        var headers = new Dictionary<string, string[]>
        {
            ["traceparent"] = [W3CHeader],
            ["tracestate"] = ["vendor=private-state"],
            ["baggage"] = ["document=private-document"],
            ["X-Amzn-Trace-Id"] = [XRayHeader]
        };
        var requested = new List<string>();

        var context = new LambdaTracePropagator().Extract(default, headers, (input, key) =>
        {
            requested.Add(key);
            return input.GetValueOrDefault(key, []);
        });

        AssertParent(context, W3CTraceId, W3CParentId);
        Assert.Multiple(() =>
        {
            Assert.That(context.ActivityContext.TraceState, Is.Null.Or.Empty);
            Assert.That(context.Baggage.GetBaggage(), Is.Empty);
            Assert.That(requested, Is.EquivalentTo(ExpectedFields));
        });
    }

    [Test]
    public void InjectsOnlyW3CWithoutMetadata()
    {
        var activity = new ActivityContext(ActivityTraceId.CreateFromString(W3CTraceId),
            ActivitySpanId.CreateFromString(W3CParentId), ActivityTraceFlags.Recorded, "vendor=private-state", isRemote: true);
        var context = new PropagationContext(activity,
            Baggage.Create(new Dictionary<string, string> { ["document"] = "private-document" }));
        var headers = new Dictionary<string, string>();
        var propagator = new LambdaTracePropagator();

        propagator.Inject(context, headers, static (output, key, value) => output[key] = value);

        Assert.Multiple(() =>
        {
            Assert.That(headers.Keys, Is.EquivalentTo(ExpectedFields));
            Assert.That(headers["traceparent"], Is.EqualTo(W3CHeader));
            Assert.That(propagator.Fields, Is.EquivalentTo(ExpectedFields).IgnoreCase);
        });
    }

    [Test]
    public void DoesNotReuseThePreviousInvocationParent()
    {
        var propagator = new LambdaTracePropagator();

        var first = propagator.Extract(Request(W3CHeader));
        var next = propagator.Extract(Request(null));

        AssertParent(first, W3CTraceId, W3CParentId);
        Assert.That(next.ActivityContext, Is.EqualTo(default(ActivityContext)));
    }

    [Test]
    public async Task ConcurrentInvocationsKeepTheirOwnW3CParents()
    {
        var propagator = new LambdaTracePropagator();
        const string otherHeader = "00-00000000000000000000000000000003-0000000000000303-01";

        async Task<PropagationContext> ExtractAsync(string header)
        {
            await Task.Yield();
            return propagator.Extract(Request(header));
        }

        var contexts = await Task.WhenAll(ExtractAsync(W3CHeader), ExtractAsync(otherHeader));

        AssertParent(contexts[0], W3CTraceId, W3CParentId);
        AssertParent(contexts[1], "00000000000000000000000000000003", "0000000000000303");
    }

    private static APIGatewayProxyRequest Request(string? traceParent) => new()
    {
        Headers = traceParent is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["traceparent"] = traceParent }
    };

    private static void AssertParent(PropagationContext context, string traceId, string parentId)
    {
        Assert.Multiple(() =>
        {
            Assert.That(context.ActivityContext.TraceId.ToString(), Is.EqualTo(traceId));
            Assert.That(context.ActivityContext.SpanId.ToString(), Is.EqualTo(parentId));
            Assert.That(context.ActivityContext.IsRemote, Is.True);
        });
    }
}
