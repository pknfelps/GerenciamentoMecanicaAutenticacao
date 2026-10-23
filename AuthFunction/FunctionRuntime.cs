using Amazon.Lambda;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Authentication;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Documents;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Email;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionRuntime : IDisposable, IAsyncDisposable
{
    private readonly IDisposable? emailClient;
    public JwtConfiguration Jwt { get; }
    public NpgsqlDataSource Database { get; }
    public RequestCustomerCodeUseCase RequestCode { get; }
    public VerifyCustomerCodeUseCase VerifyCode { get; }

    public FunctionRuntime(RuntimeConfiguration configuration, IntegrationMetrics? metrics = null,
        ICustomerCodeSender? sender = null, ILogger<PostgreSqlCustomerAccessStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Jwt = configuration.Jwt;
        Database = configuration.Database.CreateDataSource();
        try
        {
            if (sender is null)
            {
                var email = configuration.Email ?? throw new InvalidOperationException("Email configuration is required.");
                var lambda = new AmazonLambdaClient(EmailInvocationConfiguration.ClientConfiguration());
                emailClient = lambda;
                sender = new LambdaCustomerCodeSender(lambda, email.FunctionArn);
            }
            var diagnostics = new FunctionDiagnosticLogger<PostgreSqlCustomerAccessStore>(logger);
            var store = new PostgreSqlCustomerAccessStore(Database, diagnostics, metrics);
            var normalizer = new CustomerDocumentNormalizer();
            RequestCode = new RequestCustomerCodeUseCase(normalizer, store, sender,
                type => diagnostics.LogWarning("Não foi possível invalidar o desafio de autenticação. Tipo: {ExceptionType}", type));
            VerifyCode = new VerifyCustomerCodeUseCase(normalizer, store, new CustomerTokenIssuer(Jwt));
        }
        catch { emailClient?.Dispose(); Database.Dispose(); throw; }
    }
    public void Dispose() { try { emailClient?.Dispose(); } finally { Database.Dispose(); } }
    public async ValueTask DisposeAsync() { try { emailClient?.Dispose(); } finally { await Database.DisposeAsync(); } }
}
