using GerenciamentoMecanica.Autenticacao.Application.Authentication;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionRuntimeProvider : IDisposable, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<FunctionRuntime>> initialize;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FunctionRuntime? runtime;
    private int disposed;

    public FunctionRuntimeProvider(Func<CancellationToken, Task<FunctionRuntime>> initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        this.initialize = initialize;
    }

    public async Task<CustomerAuthenticationResult> ExecuteAsync(string? document, CancellationToken cancellationToken)
    {
        var context = await GetRuntimeAsync(cancellationToken);
        
        return await context.Authentication.ExecuteAsync(document, cancellationToken);
    }

    private async Task<FunctionRuntime> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        
        await gate.WaitAsync(cancellationToken);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

            if (runtime is not null)
                return runtime;

            var initialized = await initialize(cancellationToken);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                runtime = initialized;
                return runtime;
            }
            catch
            {
                await initialized.DisposeAsync();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        gate.Wait();

        try
        {
            runtime?.Dispose();
            runtime = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await gate.WaitAsync();

        try
        {
            if (runtime is not null)
                await runtime.DisposeAsync();

            runtime = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}