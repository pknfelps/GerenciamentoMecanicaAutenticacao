namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

public sealed class RuntimeConfiguration
{
    internal RuntimeConfiguration(JwtConfiguration jwt, PostgreSqlConfiguration database, EmailInvocationConfiguration? email = null)
    {
        Jwt = jwt;
        Database = database;
        Email = email;
    }

    public EmailInvocationConfiguration? Email { get; }
    public JwtConfiguration Jwt { get; }
    public PostgreSqlConfiguration Database { get; }
}
