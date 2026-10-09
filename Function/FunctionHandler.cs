using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Function.Http;
using System.Diagnostics;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionHandler : IDisposable, IAsyncDisposable
{
    private readonly FunctionRuntimeProvider? runtime;
    private readonly Func<string?, CancellationToken, Task<CustomerAuthenticationResult>> execute;

    public FunctionHandler() : this(new FunctionRuntimeProvider(new FunctionBootstrap().InitializeAsync))
    {
    }

    public FunctionHandler(FunctionRuntimeProvider runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        
        this.runtime = runtime;
        execute = runtime.ExecuteAsync;
    }

    public FunctionHandler(Func<string?, CancellationToken, Task<CustomerAuthenticationResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        
        this.execute = execute;
    }

    [LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]
    public async Task<APIGatewayProxyResponse> HandleAsync(APIGatewayProxyRequest request, ILambdaContext context)
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
                response = AuthenticationResponse.TechnicalFailure();
                
                category = error is OperationCanceledException || cancellation.IsCancellationRequested
                    ? "timeout"
                    : "technical-failure";
            }
        }

        context.Logger.LogLine(JsonSerializer.Serialize(new
        {
            lambdaRequestId = context.AwsRequestId,
            gatewayRequestId = request.RequestContext?.RequestId,
            status = response.StatusCode,
            durationMs = elapsed.ElapsedMilliseconds,
            category
        }));

        return response;
    }

    public void Dispose() => runtime?.Dispose();
    public ValueTask DisposeAsync() => runtime?.DisposeAsync() ?? ValueTask.CompletedTask;
}