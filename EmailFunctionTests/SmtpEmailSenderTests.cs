using Amazon.Lambda.Core;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using GerenciamentoMecanica.Serverless.EmailFunction;
using GerenciamentoMecanica.Serverless.EmailFunction.Smtp;
using MimeKit;
using NSubstitute;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EmailFunctionTests;

[NonParallelizable]
public sealed class SmtpEmailSenderTests
{
    private const string Configuration = """
    {
      "ServerOptions": {
        "Urls": "http://*:8080", "Port": 2525, "Database": "", "LockSettings": true,
        "AllowRemoteConnections": true, "TlsMode": "None", "AuthenticationRequired": false,
        "ImapPort": null, "Pop3Port": null, "Mailboxes": [], "Users": [], "DeliverToStdout": ""
      },
      "RelayOptions": {"SmtpServer": "", "AutomaticEmails": [], "AutomaticRelayExpression": ""},
      "Serilog": {"MinimumLevel": {"Default": "Fatal"}}
    }
    """;

    private readonly IContainer container = new ContainerBuilder("rnwood/smtp4dev:3.15.0")
        .WithWorkingDirectory("/app")
        .WithResourceMapping(Encoding.UTF8.GetBytes(Configuration), "/app/appsettings.json")
        .WithEnvironment("SERVEROPTIONS__URLS", "http://*:8080")
        .WithTmpfsMount("/smtp4dev")
        .WithPortBinding(2525, true)
        .WithPortBinding(8080, true)
        .WithCreateParameterModifier(parameters =>
        {
            var ports = parameters.HostConfig?.PortBindings ?? throw new InvalidOperationException("Portas Docker ausentes.");
            foreach (var bindings in ports.Values)
                foreach (var binding in bindings ?? [])
                    binding.HostIP = "127.0.0.1";
        })
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/")))
        .Build();
    private HttpClient http = null!;

    [OneTimeSetUp]
    public async Task StartSmtpCapture()
    {
        await container.StartAsync();
        http = new HttpClient { BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}/") };
    }

    [SetUp]
    public async Task EmptyMailbox()
    {
        using var response = await http.DeleteAsync("api/Messages/*");
        response.EnsureSuccessStatusCode();
    }

    [OneTimeTearDown]
    public async Task StopSmtpCapture()
    {
        http?.Dispose();
        await container.DisposeAsync();
    }

    private FunctionHandler Handler() => new(() => new SmtpEmailSender(EmailTestData.Settings(container.Hostname, container.GetMappedPublicPort(2525))));

    private async Task<JsonDocument> WaitForMessages(int expectedCount)
    {
        // A resposta SMTP pode chegar antes de a caixa HTTP terminar o processamento MIME.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var messages = JsonDocument.Parse(await http.GetStringAsync("api/Messages"));
            if (messages.RootElement.GetProperty("rowCount").GetInt32() == expectedCount)
                return messages;
            messages.Dispose();
            await Task.Delay(50);
        }
        throw new AssertionException("A caixa nao apresentou a quantidade esperada de mensagens capturadas.");
    }

    [TestCase("customer_access_code")]
    [TestCase("budget_available")]
    [TestCase("status_changed")]
    public async Task CapturesEachMessageTypeWithFixedSenderAndBothBodies(string type)
    {
        var message = EmailTestData.Message();
        message["type"] = type;
        var logger = Substitute.For<ILambdaLogger>();
        using var payload = EmailTestData.Payload(message);
        var response = await Handler().HandleAsync(payload, EmailTestData.Context(logger));
        Assert.That(response.Outcome, Is.EqualTo("captured"));

        using var messages = await WaitForMessages(1);
        Assert.That(messages.RootElement.GetProperty("rowCount").GetInt32(), Is.EqualTo(1));
        var captured = messages.RootElement.GetProperty("results")[0];
        Assert.That(captured.GetProperty("isRelayed").GetBoolean(), Is.False);
        var id = captured.GetProperty("id").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/Messages/{id}/raw");
        request.Headers.Accept.ParseAdd("text/plain");
        using var rawResponse = await http.SendAsync(request);
        rawResponse.EnsureSuccessStatusCode();
        var raw = await rawResponse.Content.ReadAsStringAsync();
        using var mime = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
        Assert.Multiple(() =>
        {
            Assert.That(((MailboxAddress)mime.From.Single()).Address, Is.EqualTo("noreply@mecanica.local"));
            Assert.That(((MailboxAddress)mime.From.Single()).Name, Is.EqualTo("Mecânica"));
            Assert.That(((MailboxAddress)mime.To.Single()).Address, Is.EqualTo("customer@example.invalid"));
            Assert.That(mime.Subject, Is.EqualTo(message["subject"]!.GetValue<string>()));
            Assert.That(mime.TextBody, Is.EqualTo(message["textBody"]!.GetValue<string>()));
            Assert.That(mime.HtmlBody, Is.EqualTo(message["htmlBody"]!.GetValue<string>()));
            Assert.That((string)logger.ReceivedCalls().Single().GetArguments()[0]!, Does.Not.Contain("customer@example.invalid").And.Not.Contain("01234567"));
        });
    }

    [Test]
    public async Task TextOnlyMessageDoesNotRequireHtml()
    {
        var message = EmailTestData.Message();
        message.Remove("htmlBody");
        using var payload = EmailTestData.Payload(message);
        var response = await Handler().HandleAsync(payload, EmailTestData.Context());
        Assert.That(response.Outcome, Is.EqualTo("captured"));
        using var messages = await WaitForMessages(1);
        Assert.That(messages.RootElement.GetProperty("rowCount").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentInvocationsUseSeparateConnectionsAndKeepTheirRecipients()
    {
        var handler = Handler();
        var first = EmailTestData.Message();
        var second = EmailTestData.Message();
        first["toEmail"] = "first@example.invalid";
        second["toEmail"] = "second@example.invalid";
        var firstLogger = Substitute.For<ILambdaLogger>();
        var secondLogger = Substitute.For<ILambdaLogger>();
        using var firstPayload = EmailTestData.Payload(first);
        using var secondPayload = EmailTestData.Payload(second);
        var responses = await Task.WhenAll(
            handler.HandleAsync(firstPayload, EmailTestData.Context(firstLogger)),
            handler.HandleAsync(secondPayload, EmailTestData.Context(secondLogger)));
        Assert.That(responses.All(response => response.Outcome == "captured"), Is.True);
        using var messages = await WaitForMessages(2);
        var recipients = messages.RootElement.GetProperty("results").EnumerateArray()
            .Select(message => message.GetProperty("to")[0].GetString()).ToArray();
        Assert.That(recipients, Is.EquivalentTo(new[] { "first@example.invalid", "second@example.invalid" }));
        Assert.That(firstLogger.ReceivedCalls().Count(), Is.EqualTo(1));
        Assert.That(secondLogger.ReceivedCalls().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task SmtpRefusalDoesNotReturnCapturedOrRetry()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var handler = new FunctionHandler(() => new SmtpEmailSender(EmailTestData.Settings("127.0.0.1", port)));
        using var payload = EmailTestData.Payload();
        var invocation = handler.HandleAsync(payload, EmailTestData.Context());
        using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var accepted = await server.AcceptTcpClientAsync(waiting.Token);
        await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("554 SMTP unavailable\r\n"), waiting.Token);
        var response = await invocation;
        Assert.That(response.Error, Is.EqualTo("smtp-unavailable"));
        Assert.That(server.Pending(), Is.False, "Nao deve abrir uma segunda conexao para repetir o envio.");
    }

    [Test]
    public async Task SlowSmtpIsCanceledAndItsConnectionIsClosed()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var handler = new FunctionHandler(() => new SmtpEmailSender(EmailTestData.Settings("127.0.0.1", port)));
        using var payload = EmailTestData.Payload();
        var invocation = handler.HandleAsync(payload, EmailTestData.Context(remaining: TimeSpan.FromSeconds(2)));
        using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var accepted = await server.AcceptTcpClientAsync(waiting.Token);
        // O servidor aceita TCP, mas nao envia o greeting SMTP.
        var response = await invocation;
        Assert.That(response.Error, Is.EqualTo("timeout"));
        var read = await accepted.GetStream().ReadAsync(new byte[1], waiting.Token);
        Assert.That(read, Is.Zero, "O socket SMTP deve ser fechado no timeout.");
    }
}
