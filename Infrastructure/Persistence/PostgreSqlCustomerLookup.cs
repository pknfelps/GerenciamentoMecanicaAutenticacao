using Dapper;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using Npgsql;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Persistence;

public sealed class PostgreSqlCustomerLookup(NpgsqlDataSource database) : ICustomerLookup
{
    private const string Query = """
                                 SELECT id, name, document, status
                                 FROM public.customers
                                 WHERE document = @Document;
                                 """;

    public async Task<CustomerIdentity?> FindByDocumentAsync(
        string canonicalDocument, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDocument);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        
        var command = new CommandDefinition(Query, new { Document = canonicalDocument },
            commandTimeout: 5, cancellationToken: cancellationToken);
        
        var row = await connection.QuerySingleOrDefaultAsync<CustomerRow>(command);

        if (row is null)
            return null;

        var status = row.Status switch
        {
            "Active" => CustomerStatus.Active,
            "Inactive" => CustomerStatus.Inactive,
            _ => throw new InvalidDataException("Customer status in the database is invalid.")
        };

        try
        {
            return new CustomerIdentity(row.Id, row.Name!, row.Document!, status);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("Customer identity in the database is invalid.");
        }
    }
}