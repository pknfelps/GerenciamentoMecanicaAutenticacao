using Npgsql;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

public sealed class PostgreSqlConfiguration
{
    internal string ConnectionString { get; }

    internal PostgreSqlConfiguration(NpgsqlConnectionStringBuilder connection)
    {
        Host = connection.Host!;
        Port = connection.Port;
        Database = connection.Database!;
        Username = connection.Username!;
        ConnectionString = connection.ConnectionString;
    }

    public string Host { get; }
    public int Port { get; }
    public string Database { get; }
    public string Username { get; }

    // Creating a data source does not open a PostgreSQL connection.
    public NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString);
}
