using GerenciamentoMecanica.Serverless.EmailFunction.Contracts;
using System.Text;
using System.Text.Json.Nodes;

namespace EmailFunctionTests;

public sealed class EmailRequestParserTests
{
    private static async Task<EmailRequest?> Parse(JsonObject message)
    {
        using var payload = EmailTestData.Payload(message);
        return await EmailRequestParser.ParseAsync(payload, CancellationToken.None);
    }

    [TestCase("customer_access_code")]
    [TestCase("budget_available")]
    [TestCase("status_changed")]
    public async Task AcceptsEachSupportedMessageType(string type)
    {
        var message = EmailTestData.Message();
        message["type"] = type;
        var parsed = await Parse(message);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Type, Is.EqualTo(type));
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(-1)]
    public async Task RejectsAnUnsupportedContractVersion(int version)
    {
        var message = EmailTestData.Message();
        message["version"] = version;
        Assert.That(await Parse(message), Is.Null);
    }

    [Test]
    public async Task BodyLineBreaksArePreservedInsteadOfBeingTreatedAsHeaders()
    {
        var message = EmailTestData.Message();
        message["textBody"] = "Primeira linha.\r\nSegunda linha.";
        Assert.That((await Parse(message))!.TextBody, Is.EqualTo("Primeira linha.\r\nSegunda linha."));
    }

    [TestCase("version")]
    [TestCase("type")]
    [TestCase("toName")]
    [TestCase("toEmail")]
    [TestCase("subject")]
    [TestCase("textBody")]
    public async Task RejectsMissingOrNullRequiredProperty(string property)
    {
        var message = EmailTestData.Message();
        message.Remove(property);
        Assert.That(await Parse(message), Is.Null);
        message[property] = null;
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("host")]
    [TestCase("port")]
    [TestCase("senderEmail")]
    [TestCase("cc")]
    [TestCase("bcc")]
    [TestCase("attachments")]
    [TestCase("Type")]
    public async Task RejectsUnknownPropertiesIncludingConnectionOptions(string property)
    {
        var message = EmailTestData.Message();
        message[property] = "not-accepted";
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("version")]
    [TestCase("toEmail")]
    [TestCase("textBody")]
    public async Task RejectsDuplicatePropertiesEvenWithTheSameValue(string property)
    {
        var message = EmailTestData.Message();
        var json = message.ToJsonString();
        json = json[..^1] + ",\"" + property + "\":" + message[property]!.ToJsonString() + "}";
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.That(await EmailRequestParser.ParseAsync(payload, CancellationToken.None), Is.Null);
    }

    [TestCase("toName")]
    [TestCase("toEmail")]
    [TestCase("subject")]
    public async Task RejectsHeaderControlCharacters(string property)
    {
        var message = EmailTestData.Message();
        message[property] = "test\r\nBcc: another@example.invalid";
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("not-an-email")]
    [TestCase("customer@example.invalid,other@example.invalid")]
    [TestCase("Name <customer@example.invalid>")]
    [TestCase(" customer@example.invalid")]
    public async Task RejectsAnythingOtherThanOneBareMailbox(string address)
    {
        var message = EmailTestData.Message();
        message["toEmail"] = address;
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("toName", 101)]
    [TestCase("subject", 201)]
    [TestCase("toEmail", 255)]
    public async Task RejectsHeadersAboveTheirLengthLimit(string property, int length)
    {
        var message = EmailTestData.Message();
        message[property] = new string('a', length);
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("subject")]
    [TestCase("textBody")]
    public async Task RejectsBlankRequiredContent(string property)
    {
        var message = EmailTestData.Message();
        message[property] = "   ";
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("textBody", 32768)]
    [TestCase("htmlBody", 65536)]
    public async Task BodyLimitsCountUtf8BytesRatherThanCharacters(string property, int maximumBytes)
    {
        var message = EmailTestData.Message();
        message[property] = new string('é', maximumBytes / 2);
        Assert.That(await Parse(message), Is.Not.Null, "O limite exato em bytes deve ser aceito.");
        message[property] = new string('é', maximumBytes / 2 + 1);
        Assert.That(await Parse(message), Is.Null, "Um caractere adicional ultrapassa o limite UTF-8.");
    }

    [Test]
    public async Task AcceptsEmptyRecipientNameAndOptionalHtml()
    {
        var message = EmailTestData.Message();
        message["toName"] = "";
        message.Remove("htmlBody");
        Assert.That((await Parse(message))!.HtmlBody, Is.Null);
        message["htmlBody"] = null;
        Assert.That((await Parse(message))!.HtmlBody, Is.Null);
    }

    [TestCase("version")]
    [TestCase("type")]
    [TestCase("toName")]
    [TestCase("toEmail")]
    [TestCase("subject")]
    [TestCase("textBody")]
    [TestCase("htmlBody")]
    public async Task RejectsIncorrectJsonTypes(string property)
    {
        var message = EmailTestData.Message();
        message[property] = true;
        Assert.That(await Parse(message), Is.Null);
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("null")]
    public async Task RejectsMalformedOrNonObjectPayload(string json)
    {
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.That(await EmailRequestParser.ParseAsync(payload, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task RejectsInvalidUtf8InAField()
    {
        var bytes = EmailTestData.Payload().ToArray();
        var index = Array.IndexOf(bytes, (byte)'C');
        bytes[index] = 0xff;
        using var payload = new MemoryStream(bytes);
        Assert.That(await EmailRequestParser.ParseAsync(payload, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task AcceptsEnvelopeAtExactly128KiB()
    {
        using var message = EmailTestData.Payload();
        var bytes = message.ToArray();
        using var payload = new MemoryStream();
        payload.Write(bytes);
        payload.Write(Enumerable.Repeat((byte)' ', EmailRequestParser.MaximumPayloadBytes - bytes.Length).ToArray());
        payload.Position = 0;
        Assert.That(await EmailRequestParser.ParseAsync(payload, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public async Task StopsReadingAnOversizedPayloadAfterTheLimitPlusOneByte()
    {
        using var payload = new MemoryStream(new byte[2 * 1024 * 1024]);
        Assert.That(await EmailRequestParser.ParseAsync(payload, CancellationToken.None), Is.Null);
        Assert.That(payload.Position, Is.EqualTo(EmailRequestParser.MaximumPayloadBytes + 1));
    }
}
