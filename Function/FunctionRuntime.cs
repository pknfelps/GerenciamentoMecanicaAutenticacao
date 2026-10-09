using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Authentication;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Documents;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using Npgsql;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionRuntime : IDisposable, IAsyncDisposable
{
    public JwtConfiguration Jwt { get; }
    public NpgsqlDataSource Database { get; }
    public ValidateCustomerDocumentUseCase Authentication { get; }
    
    public FunctionRuntime(RuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        
        Jwt = configuration.Jwt;
        Database = configuration.Database.CreateDataSource();
        
        Authentication = new ValidateCustomerDocumentUseCase(
            new CustomerDocumentNormalizer(), new PostgreSqlCustomerLookup(Database),
            new CustomerTokenIssuer(Jwt));
    }

    public void Dispose() => Database.Dispose();
    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
