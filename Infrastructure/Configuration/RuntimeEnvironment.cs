namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

public sealed class RuntimeEnvironment
{
    public const string AccountId = "121754142617";
    public const string SupportedRegion = "us-east-1";

    private RuntimeEnvironment(string name) => Name = name;

    public string Name { get; }
    public string Region => SupportedRegion;

    public static RuntimeEnvironment Parse(string? environment, string? region)
    {
        if (environment is not ("hom" or "prd"))
            throw new InvalidOperationException("Runtime__Environment must be hom or prd.");
        
        if (region != SupportedRegion)
            throw new InvalidOperationException("AWS_REGION must be us-east-1.");
        
        return new RuntimeEnvironment(environment);
    }
}
