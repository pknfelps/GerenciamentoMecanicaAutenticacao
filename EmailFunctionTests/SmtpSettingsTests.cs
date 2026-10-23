using GerenciamentoMecanica.Serverless.EmailFunction.Configuration;

namespace EmailFunctionTests;

public sealed class SmtpSettingsTests
{
    [Test]
    public void LocalConfigurationUsesFixedSenderDefaults()
    {
        var settings = SmtpSettings.Load(key => key == "Email__SmtpHost" ? "smtp" : null);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Host, Is.EqualTo("smtp"));
            Assert.That(settings.Port, Is.EqualTo(25));
            Assert.That(settings.SenderName, Is.EqualTo("Mecânica"));
            Assert.That(settings.SenderEmail, Is.EqualTo("noreply@mecanica.local"));
        });
    }

    [TestCase("hom")]
    [TestCase("prd")]
    public void AwsConfigurationUsesTheSameSmtpTransport(string environment)
    {
        var settings = SmtpSettings.Load(key => key switch
        {
            "Runtime__Environment" => environment,
            "AWS_REGION" => "us-east-1",
            "Email__SmtpHost" => "internal-example.elb.us-east-1.amazonaws.com",
            "Email__SmtpPort" => "2525",
            _ => null
        });
        Assert.That(settings.Port, Is.EqualTo(2525));
    }

    [TestCase("Email__SmtpHost", "")]
    [TestCase("Email__SmtpHost", "https://smtp.example.invalid")]
    [TestCase("Email__SmtpHost", "smtp:25")]
    [TestCase("Email__SmtpHost", " smtp")]
    [TestCase("Email__SmtpPort", "")]
    [TestCase("Email__SmtpPort", "0")]
    [TestCase("Email__SmtpPort", "65536")]
    [TestCase("Email__SmtpPort", "not-a-port")]
    [TestCase("Email__SenderEmail", "Name <sender@example.invalid>")]
    [TestCase("Email__SenderEmail", "sender@example.invalid,other@example.invalid")]
    [TestCase("Email__SenderName", "Name\r\nBcc: x@example.invalid")]
    [TestCase("Runtime__Environment", "")]
    [TestCase("Runtime__Environment", "local")]
    public void InvalidConfigurationHasAFixedMessageWithoutTheValue(string name, string value)
    {
        var values = new Dictionary<string, string?> { ["Email__SmtpHost"] = "smtp", [name] = value };
        var error = Assert.Throws<InvalidOperationException>(() => SmtpSettings.Load(key => values.GetValueOrDefault(key)));
        Assert.That(error!.Message, Is.EqualTo(SmtpSettings.InvalidConfigurationMessage));
        Assert.That(error.InnerException, Is.Null);
    }

    [TestCase(null)]
    [TestCase("sa-east-1")]
    public void AwsModeRequiresTheExpectedRegion(string? region)
    {
        Assert.Throws<InvalidOperationException>(() => SmtpSettings.Load(key => key switch
        {
            "Runtime__Environment" => "hom",
            "AWS_REGION" => region,
            "Email__SmtpHost" => "smtp",
            _ => null
        }));
    }

    [Test]
    public void MissingHostDoesNotFallBackToAnExternalMailServer()
    {
        Assert.Throws<InvalidOperationException>(() => SmtpSettings.Load(_ => null));
    }
}
