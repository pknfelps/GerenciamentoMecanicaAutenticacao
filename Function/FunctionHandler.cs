using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Function.Http;
using GerenciamentoMecanica.Autenticacao.Function.Observability;
using System.Diagnostics;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionHandler : IDisposable, IAsyncDisposable
{
    private readonly FunctionRuntimeProvider? runtime;
    private readonly LambdaObservability? observability;
    private readonly Func<string?, CancellationToken, Task<CustomerAuthenticationResult>> execute;

    public FunctionHandler() : this(LambdaObservability.CreateDefault())
    {
    }

    private FunctionHandler(LambdaObservability? observability) : this(
        new FunctionRuntimeProvider(new FunctionBootstrap(observability?.Integrations).InitializeAsync), observability)
    {
    }

    public FunctionHandler(FunctionRuntimeProvider runtime, LambdaObservability? observability = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        
        this.runtime = runtime;
        this.observability = observability;
        execute = runtime.ExecuteAsync;
    }

    public FunctionHandler(Func<string?, CancellationToken, Task<CustomerAuthenticationResult>> execute, LambdaObservability? observability = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        
        this.execute = execute;
        this.observability = observability;
    }

    [LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]
    public Task<APIGatewayProxyResponse> HandleAsync(APIGatewayProxyRequest request, ILambdaContext context) =>
        observability is null ? HandleCoreAsync(request, context) : observability.InvokeAsync(request, context, HandleCoreAsync);

    private async Task<APIGatewayProxyResponse> HandleCoreAsync(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var elapsed = Stopwatch.StartNew();
        
        APIGatewayProxyResponse response;
        
        var category = "invalid-request";

        if (!CustomerValidationRequestParser.TryParse(request, out var document))
        {
            response = AuthenticationResponse.InvalidRequest();
        }
        else
        {
            var budget = context.RemainingTime - TimeSpan.FromSeconds(1);
            using var cancellation = new CancellationTokenSource();

            try
            {
                if (budget <= TimeSpan.Zero)
                    throw new OperationCanceledException();

                cancellation.CancelAfter(budget);
                var result = await execute(document, cancellation.Token).WaitAsync(cancellation.Token);
                
                cancellation.Token.ThrowIfCancellationRequested();
                
                response = AuthenticationResponse.FromResult(result);
                category = result.Outcome switch
                {
                    CustomerAuthenticationOutcome.Authenticated => "authenticated",
                    CustomerAuthenticationOutcome.NotEligible => "not-eligible",
                    _ => "invalid-document"
                };
            }
            catch (Exception error)
            {
                observability?.RecordException(error);
                response = AuthenticationResponse.TechnicalFailure();
                
                category = error is OperationCanceledException || cancellation.IsCancellationRequested
                    ? "timeout"
                    : "technical-failure";
            }
        }

        if (observability is not null)
            observability.Complete(context, request, response.StatusCode, category, elapsed.Elapsed);
        else
        {
            context.Logger.LogLine(JsonSerializer.Serialize(new
            {
                lambdaRequestId = context.AwsRequestId,
                gatewayRequestId = request.RequestContext?.RequestId,
                status = response.StatusCode,
                durationMs = elapsed.ElapsedMilliseconds,
                category
            }));
        }

        return response;
    }

    public void Dispose()
    {
        try { runtime?.Dispose(); }
        finally { observability?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
        }
        finally { observability?.Dispose(); }
    }
}
