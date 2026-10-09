using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using Npgsql;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionRuntime : IDisposable, IAsyncDisposable
{
    public FunctionRuntime(RuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Jwt = configuration.Jwt;
        Database = configuration.Database.CreateDataSource();
    }

    public JwtConfiguration Jwt { get; }
    public NpgsqlDataSource Database { get; }

    public void Dispose() => Database.Dispose();
    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
