namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

// Regular classes deliberately keep keys and connection strings out of ToString().
public sealed class JwtConfiguration
{
    internal JwtConfiguration(string key, string issuer, string audience)
    {
        Key = key;
        Issuer = issuer;
        Audience = audience;
    }

    public string Key { get; }
    public string Issuer { get; }
    public string Audience { get; }
}
