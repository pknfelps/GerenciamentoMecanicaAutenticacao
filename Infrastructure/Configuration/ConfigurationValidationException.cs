namespace GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3871",
    Justification =
        "Internal validation marker is converted to a sanitized InvalidOperationException before leaving the loader.")]
internal sealed class ConfigurationValidationException(string message) : Exception(message);
