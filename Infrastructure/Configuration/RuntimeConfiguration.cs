namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

public sealed class RuntimeConfiguration
{
    internal RuntimeConfiguration(JwtConfiguration jwt, PostgreSqlConfiguration database)
    {
        Jwt = jwt;
        Database = database;
    }

    public JwtConfiguration Jwt { get; }
    public PostgreSqlConfiguration Database { get; }
}
