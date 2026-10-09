using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Authentication;
using GerenciamentoMecanica.Autenticacao.Infrastructure.Configuration;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace GerenciamentoMecanica.Autenticacao.InfrastructureTests;

public class CustomerTokenIssuerTests
{
    private const string Key = "test-only-signing-key-with-at-least-32-characters";

    private static TokenValidationParameters Validation(string key = Key, string issuer = "test-issuer",
        string audience = "test-audience") => new()
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
    };

    [Test]
    public void RealTokenUsesStoredIdentityAndSharedAlgorithm()
    {
        var customer =
            new CustomerIdentity(Guid.NewGuid(), "Nome do cadastro", "529.982.247-25", CustomerStatus.Active);

        var token =
            new CustomerTokenIssuer(new JwtConfiguration(Key, "test-issuer", "test-audience")).Generate(customer);

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, Validation(), out var validated);

        Assert.Multiple(() =>
        {
            Assert.That(principal.Identity!.Name, Is.EqualTo(customer.Name));
            Assert.That(principal.IsInRole("Customer"), Is.True);
            Assert.That(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value, Is.EqualTo(customer.Id.ToString()));
            Assert.That(((JwtSecurityToken)validated).Header.Alg, Is.EqualTo("HS256"));
            Assert.That(principal.Claims.Count(claim => claim.Type == ClaimTypes.Role), Is.EqualTo(1));
            Assert.That(principal.Claims.Any(claim => claim.Value == customer.Document), Is.False);
        });
    }

    [Test]
    public void ExpirationUsesProvidedClockPlusTenMinutes()
    {
        var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var clock = Substitute.For<TimeProvider>();

        clock.GetUtcNow().Returns(now);

        var token = new CustomerTokenIssuer(new JwtConfiguration(Key, "test-issuer", "test-audience"), clock)
            .Generate(new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-25", CustomerStatus.Active));

        Assert.That(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo,
            Is.EqualTo(now.UtcDateTime.AddMinutes(10)));
    }

    [TestCase("key")]
    [TestCase("issuer")]
    [TestCase("audience")]
    public void OtherConfigurationDoesNotValidateToken(string mismatch)
    {
        var token = new CustomerTokenIssuer(new JwtConfiguration(Key, "test-issuer", "test-audience"))
            .Generate(new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-25", CustomerStatus.Active));

        var validation = Validation(
            mismatch == "key" ? "other-test-key-with-at-least-32-characters" : Key,
            mismatch == "issuer" ? "other-issuer" : "test-issuer",
            mismatch == "audience" ? "other-audience" : "test-audience");

        Assert.Catch<SecurityTokenException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(token, validation, out _));
    }

    [Test]
    public void ExpiredTokenIsRejectedWithUnchangedDefaultClockSkew()
    {
        var clock = Substitute.For<TimeProvider>();

        clock.GetUtcNow().Returns(DateTimeOffset.UtcNow.AddMinutes(-30));

        var token = new CustomerTokenIssuer(new JwtConfiguration(Key, "test-issuer", "test-audience"), clock)
            .Generate(new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-25", CustomerStatus.Active));

        Assert.Throws<SecurityTokenExpiredException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(token, Validation(), out _));
    }

    [Test]
    public void InactiveOrInvalidDocumentCannotProduceToken()
    {
        var issuer = new CustomerTokenIssuer(new JwtConfiguration(Key, "test-issuer", "test-audience"));

        Assert.Throws<InvalidOperationException>(() => issuer.Generate(
            new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-25", CustomerStatus.Inactive)));

        Assert.Throws<InvalidDataException>(() => issuer.Generate(
            new CustomerIdentity(Guid.NewGuid(), "Cliente", "529.982.247-24", CustomerStatus.Active)));
    }
}