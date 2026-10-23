using Amazon;
using Amazon.Lambda;
using Amazon.Runtime;

namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

public sealed class EmailInvocationConfiguration
{
    private EmailInvocationConfiguration(string functionArn) => FunctionArn = functionArn;
    public string FunctionArn { get; }
    public static EmailInvocationConfiguration Parse(RuntimeEnvironment environment, string arn, string version)
    {
        var expected = $"arn:aws:lambda:{environment.Region}:{RuntimeEnvironment.AccountId}:function:mecanica-{environment.Name}-email:live";
        if (version != "1" || arn != expected)
            throw new InvalidOperationException("Email invocation configuration is invalid.");
        return new EmailInvocationConfiguration(arn);
    }
    public static AmazonLambdaConfig ClientConfiguration() => new()
    {
        RegionEndpoint = RegionEndpoint.USEast1,
        RetryMode = RequestRetryMode.Standard,
        MaxErrorRetry = 0,
        IgnoreConfiguredEndpointUrls = true,
        Timeout = TimeSpan.FromSeconds(5),
        LogResponse = false,
        LogMetrics = false
    };
}
