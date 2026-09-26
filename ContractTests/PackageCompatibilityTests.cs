using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GerenciamentoMecanica.Auth.Contracts;
using Microsoft.IdentityModel.Tokens;

namespace ContractTests;

public class PackageCompatibilityTests
{
    [TestCase("52998224725", "529.982.247-25", DocumentType.Cpf)]
    [TestCase("529.982.247-25", "529.982.247-25", DocumentType.Cpf)]
    [TestCase("10359666000194", "10.359.666/0001-94", DocumentType.Cnpj)]
    [TestCase("10.359.666/0001-94", "10.359.666/0001-94", DocumentType.Cnpj)]
    public void PackageProducesDatabaseDocumentFormat(string input, string expected, DocumentType type)
    {
        var document = DocumentRules.Parse(input);
        Assert.That(document.Type, Is.EqualTo(type));
        Assert.That(document.Value, Is.EqualTo(expected));
        Assert.That(DocumentRules.Normalize(expected), Is.EqualTo(expected));
    }

    [TestCase("52998224724")]
    [TestCase("10359666000195")]
    [TestCase("1035966600019")]
    [TestCase("x10359666000194")]
    public void PackageRejectsInvalidCustomerDocument(string input) =>
        Assert.Throws<FormatException>(() => DocumentRules.Normalize(input));

    [Test]
    public void PackageExposesSpecificRulesForBothDocumentTypes()
    {
        Assert.That(CpfRules.Normalize("52998224725"), Is.EqualTo("529.982.247-25"));
        Assert.That(CnpjRules.Normalize("10359666000194"), Is.EqualTo("10.359.666/0001-94"));
        Assert.Throws<FormatException>(() => CpfRules.Normalize("10359666000194"));
        Assert.Throws<FormatException>(() => CnpjRules.Normalize("52998224725"));
    }

    [Test]
    public void PackageProducesCustomerIdentityCompatibleWithJwtValidation()
    {
        const string key = "consumer-test-only-key-at-least-32-characters";
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var before = DateTime.UtcNow;
        var token = new JwtIssuer(key, "test-issuer", "test-audience").Generate("cliente", "Customer", id);
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = "test-issuer",
            ValidateAudience = true, ValidAudience = "test-audience",
            ValidateLifetime = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
        }, out var validated);
        Assert.That(principal.Identity!.Name, Is.EqualTo("cliente"));
        Assert.That(principal.IsInRole("Customer"), Is.True);
        Assert.That(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value, Is.EqualTo(id.ToString()));
        Assert.That(((JwtSecurityToken)validated).Header.Alg, Is.EqualTo("HS256"));
        Assert.That(validated.ValidTo, Is.InRange(before.AddMinutes(10).AddSeconds(-1), DateTime.UtcNow.AddMinutes(10)));
    }
}
