using GerenciamentoMecanica.Autenticacao.Infrastructure.Observability;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Npgsql;
using System.Globalization;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

public sealed class AwsRuntimeConfigurationLoader(
    IAmazonSimpleSystemsManagement parameters,
    IAmazonSecretsManager secrets, IntegrationMetrics? metrics = null)
{
    public static TimeSpan InitializationTimeout => TimeSpan.FromSeconds(8);

    public async Task<RuntimeConfiguration> LoadAsync(
        RuntimeEnvironment environment,
        string certificatePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(InitializationTimeout);

        var stage = "read runtime parameters";

        try
        {
            deadline.Token.ThrowIfCancellationRequested();

            var basePrefix = $"/mecanica/{environment.Name}/base/v2/";
            var databasePrefix = $"/mecanica/{environment.Name}/database/v2/";

            string[] names =
            [
                basePrefix + "jwt-secret-arn", basePrefix + "jwt-issuer", basePrefix + "jwt-audience",
                databasePrefix + "endpoint", databasePrefix + "port", databasePrefix + "database-name",
                databasePrefix + "auth-secret-arn", databasePrefix + "auth-db-user", databasePrefix + "ssl-mode"
            ];

            var response = await IntegrationMetrics.MeasureAsync(metrics, "ssm", "get_parameters", "bootstrap",
                () => parameters.GetParametersAsync(new GetParametersRequest
            {
                Names = [.. names],
                WithDecryption = false
            }, deadline.Token).WaitAsync(deadline.Token), cancellationToken);

            var values = ReadParameters(response, names);
            var jwtArn = values[basePrefix + "jwt-secret-arn"];
            var databaseArn = values[databasePrefix + "auth-secret-arn"];

            ValidateSecretArn(jwtArn, environment, "base/jwt");
            ValidateSecretArn(databaseArn, environment, "database/auth");

            if (values[databasePrefix + "auth-db-user"] != "mecanica_auth")
                throw new ConfigurationValidationException("Database auth user must be mecanica_auth.");

            if (values[databasePrefix + "ssl-mode"] != "verify-full")
                throw new ConfigurationValidationException("Database SSL mode must be verify-full.");

            if (!File.Exists(certificatePath))
                throw new ConfigurationValidationException("Published RDS root certificate is missing.");

            if (!int.TryParse(values[databasePrefix + "port"], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var port) || port is < 1 or > 65535)
                throw new ConfigurationValidationException("Database port is invalid.");

            stage = "read JWT secret";

            using var jwtDocument = await ReadSecretAsync(jwtArn, deadline.Token, cancellationToken);

            var key = ReadField(jwtDocument.RootElement, "key");

            if (key.Length < 32)
                throw new ConfigurationValidationException("JWT signing key must contain at least 32 characters.");

            var jwt = new JwtConfiguration(key, values[basePrefix + "jwt-issuer"], values[basePrefix + "jwt-audience"]);

            stage = "read database auth secret";

            using var databaseDocument = await ReadSecretAsync(databaseArn, deadline.Token, cancellationToken);

            var username = ReadField(databaseDocument.RootElement, "username");
            var password = ReadField(databaseDocument.RootElement, "password");

            if (username != values[databasePrefix + "auth-db-user"])
                throw new ConfigurationValidationException("Database secret username must match mecanica_auth.");

            stage = "prepare the PostgreSQL configuration";

            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = values[databasePrefix + "endpoint"],
                Port = port,
                Database = values[databasePrefix + "database-name"],
                Username = username,
                Password = password,
                SslMode = SslMode.VerifyFull,
                RootCertificate = certificatePath,
                GssEncryptionMode = GssEncryptionMode.Disable,
                Pooling = false,
                Timeout = 5,
                CommandTimeout = 5
            };

            deadline.Token.ThrowIfCancellationRequested();

            return new RuntimeConfiguration(jwt, new PostgreSqlConfiguration(connection));
        }
        catch (ConfigurationValidationException exception)
        {
            throw new InvalidOperationException(exception.Message);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                "AWS runtime configuration was canceled or exceeded the 8-second deadline.");
        }
        catch (Exception)
        {
            // SDK/JSON exceptions can contain response bodies. Never retain them in startup errors.
            throw new InvalidOperationException($"Unable to {stage}. Check runtime configuration and access.");
        }
    }

    private async Task<JsonDocument> ReadSecretAsync(string arn, CancellationToken cancellationToken, CancellationToken callerCancellation)
    {
        var response = await IntegrationMetrics.MeasureAsync(metrics, "secretsmanager", "get_secret_value", "bootstrap",
            () => secrets.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = arn,
            VersionStage = "AWSCURRENT"
        }, cancellationToken).WaitAsync(cancellationToken), callerCancellation);

        if (string.IsNullOrWhiteSpace(response.SecretString))
            throw new ConfigurationValidationException("Secret must contain a JSON object.");

        return JsonDocument.Parse(response.SecretString);
    }

    private static string ReadField(JsonElement document, string field)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(field, out var value)
                                                       || value.ValueKind != JsonValueKind.String ||
                                                       string.IsNullOrWhiteSpace(value.GetString()))
            throw new ConfigurationValidationException($"Secret field '{field}' is missing or invalid.");

        return value.GetString()!;
    }

    private static Dictionary<string, string> ReadParameters(GetParametersResponse response, string[] names)
    {
        if (response.InvalidParameters is { Count: > 0 })
            throw new ConfigurationValidationException("AWS reported invalid runtime parameters.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var matches = response.Parameters?.Where(parameter => parameter.Name == name).ToArray();

            if (matches is not { Length: 1 } || matches[0].Type != ParameterType.String
                                             || string.IsNullOrWhiteSpace(matches[0].Value))
                throw new ConfigurationValidationException($"Required String parameter is missing or invalid: {name}.");

            values.Add(name, matches[0].Value);
        }

        return values;
    }

    private static void ValidateSecretArn(string arn, RuntimeEnvironment environment, string name)
    {
        var prefix =
            $"arn:aws:secretsmanager:{environment.Region}:{RuntimeEnvironment.AccountId}:secret:/mecanica/{environment.Name}/{name}-";

        if (!arn.StartsWith(prefix, StringComparison.Ordinal))
            throw new ConfigurationValidationException(
                "Secret ARN must match the expected account, region, environment and resource.");

        var suffix = arn.AsSpan(prefix.Length);

        if (suffix.Length != 6 || !suffix.ToArray().All(char.IsAsciiLetterOrDigit))
            throw new ConfigurationValidationException("Secret ARN has an invalid resource suffix.");
    }

}