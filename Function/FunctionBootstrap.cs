using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SimpleSystemsManagement;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

namespace GerenciamentoMecanica.Autenticacao.Function;

public sealed class FunctionBootstrap
{
    private readonly Func<string, string?> readEnvironment;
    private readonly Func<RegionEndpoint, IAmazonSimpleSystemsManagement> createParameters;
    private readonly Func<RegionEndpoint, IAmazonSecretsManager> createSecrets;

    public FunctionBootstrap() : this(
        Environment.GetEnvironmentVariable,
        region => new AmazonSimpleSystemsManagementClient(new AmazonSimpleSystemsManagementConfig
        {
            RegionEndpoint = region,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2
        }),
        region => new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig
        {
            RegionEndpoint = region,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2
        }))
    {
    }

    public FunctionBootstrap(
        Func<string, string?> readEnvironment,
        Func<RegionEndpoint, IAmazonSimpleSystemsManagement> createParameters,
        Func<RegionEndpoint, IAmazonSecretsManager> createSecrets)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        ArgumentNullException.ThrowIfNull(createParameters);
        ArgumentNullException.ThrowIfNull(createSecrets);
        this.readEnvironment = readEnvironment;
        this.createParameters = createParameters;
        this.createSecrets = createSecrets;
    }

    public async Task<FunctionRuntime> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var environment = RuntimeEnvironment.Parse(
            readEnvironment("Runtime__Environment"), readEnvironment("AWS_REGION"));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var region = RegionEndpoint.GetBySystemName(environment.Region);
            using var parameters = createParameters(region);
            using var secrets = createSecrets(region);
            var certificate = Path.Combine(AppContext.BaseDirectory, "Certificates", "rds-ca.pem");
            var configuration = await new AwsRuntimeConfigurationLoader(parameters, secrets)
                .LoadAsync(environment, certificate, cancellationToken);
            return new FunctionRuntime(configuration);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Unable to initialize function runtime. Check configuration, credentials and access.");
        }
    }
}
