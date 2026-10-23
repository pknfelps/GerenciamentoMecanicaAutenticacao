using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Function;
using GerenciamentoMecanica.Autenticacao.Function.Http;
using NSubstitute;
using System.Text;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests;

public class FunctionHandlerTests
{
    private const string Document = "52998224725";
    private static readonly Guid Challenge = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private ILambdaContext context = null!;
    private ILambdaLogger logger = null!;
    private int requests, verifications;
    private string? observedDocument;
    [SetUp]
    public void Setup()
    {
        context = Substitute.For<ILambdaContext>();
        logger = Substitute.For<ILambdaLogger>();
        context.RemainingTime.Returns(TimeSpan.FromSeconds(15));
        context.Logger.Returns(logger);
        context.AwsRequestId.Returns("lambda-request");
        requests = verifications = 0;
        observedDocument = null;
    }
    private FunctionHandler Handler(CustomerAuthenticationResult? verify = null, CustomerCodeRequestResponse? request = null) => new(
        (document, token) => { requests++; observedDocument = document; return Task.FromResult(request ?? CustomerCodeRequestResponse.Accepted(Challenge)); },
        (id, document, code, token) => { verifications++; observedDocument = document; Assert.That(id, Is.EqualTo(Challenge)); Assert.That(code, Is.EqualTo("01234567")); return Task.FromResult(verify ?? CustomerAuthenticationResult.Authenticated("test.jwt.token")); });
    private static APIGatewayProxyRequest Request(string route = AuthenticationRoute.RequestCode, string? body = null) => new()
    {
        HttpMethod = "POST",
        Path = route,
        Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
        Body = body ?? (route == AuthenticationRoute.VerifyCode ? JsonSerializer.Serialize(new { document = Document, challengeId = Challenge, code = "01234567" }) : JsonSerializer.Serialize(new { document = Document })),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { RequestId = "gateway-request" }
    };
    private string Log() => (string)logger.ReceivedCalls().Single().GetArguments()[0]!;
    private static void AssertResponse(APIGatewayProxyResponse response, int status)
    {
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(response.Headers["Cache-Control"], Is.EqualTo("no-store"));
        Assert.That(response.IsBase64Encoded, Is.False);
        Assert.That(response.Headers["Content-Type"], Is.EqualTo(status is 200 or 202 ? "application/json" : "application/problem+json"));
    }
    private static void AssertProblem(APIGatewayProxyResponse response, int status, string route)
    {
        AssertResponse(response, status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.That(json.RootElement.GetProperty("instance").GetString(), Is.EqualTo(route));
        Assert.That(json.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(status));
        Assert.That(json.RootElement.GetProperty("type").GetString(), Is.EqualTo("about:blank"));
        Assert.That(json.RootElement.EnumerateObject().Count(), Is.EqualTo(5));
    }
    [Test]
    public async Task CodeRequestReturnsOnlyOpaqueIdAndFixedMessage()
    {
        using var handler = Handler();
        var response = await handler.HandleAsync(Request(), context);
        AssertResponse(response, 202);
        using var body = JsonDocument.Parse(response.Body);
        Assert.That(body.RootElement.GetProperty("challengeId").GetGuid(), Is.EqualTo(Challenge));
        Assert.That(body.RootElement.GetProperty("message").GetString(), Is.EqualTo("Se o cadastro estiver apto, um código será enviado."));
        Assert.That(body.RootElement.EnumerateObject().Count(), Is.EqualTo(2));
        Assert.That(requests, Is.EqualTo(1));
        Assert.That(verifications, Is.Zero);
        Assert.That(Log(), Does.Contain("code-requested").And.Not.Contain(Document).And.Not.Contain(Challenge.ToString()));
    }
    [Test]
    public async Task VerificationReturnsJsonStringTokenAndSafeCompletion()
    {
        using var handler = Handler();
        var response = await handler.HandleAsync(Request(AuthenticationRoute.VerifyCode), context);
        AssertResponse(response, 200);
        Assert.That(JsonSerializer.Deserialize<string>(response.Body), Is.EqualTo("test.jwt.token"));
        Assert.That(verifications, Is.EqualTo(1));
        Assert.That(requests, Is.Zero);
        using var json = JsonDocument.Parse(Log());
        Assert.That(json.RootElement.GetProperty("category").GetString(), Is.EqualTo("authenticated"));
        Assert.That(json.RootElement.EnumerateObject().Count(), Is.EqualTo(5));
        Assert.That(Log(), Does.Not.Contain("test.jwt").And.Not.Contain("01234567").And.Not.Contain(Document));
    }
    [TestCase("/customers/validate")]
    [TestCase("/customers/auth/unknown?document=sensitive")]
    [TestCase("")]
    public async Task RemovedOrUnknownRouteNeverExecutes(string route)
    {
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(Request(route), context), 404, AuthenticationRoute.Unknown);
        Assert.That(requests + verifications, Is.Zero);
        Assert.That(Log(), Does.Not.Contain(route.Length > 0 ? route : "sensitive"));
    }
    [TestCase(AuthenticationRoute.RequestCode)]
    [TestCase(AuthenticationRoute.VerifyCode)]
    public async Task WrongMethodReturnsAllowPostWithoutExecuting(string route)
    {
        using var handler = Handler();
        var request = Request(route);
        request.HttpMethod = "GET";
        var response = await handler.HandleAsync(request, context);
        AssertProblem(response, 405, route);
        Assert.That(response.Headers["Allow"], Is.EqualTo("POST"));
        Assert.That(requests + verifications, Is.Zero);
    }
    [TestCase(null)]
    [TestCase("")]
    [TestCase("text/plain")]
    [TestCase("application/problem+json")]
    [TestCase("application/json; charset=latin-1")]
    [TestCase("application/json; charset=utf-8; charset=utf-8")]
    [TestCase("application/json; boundary=unexpected")]
    public async Task InvalidContentTypeDoesNotInitialize(string? value)
    {
        using var handler = Handler();
        var request = Request();
        request.Headers = value is null ? null : new Dictionary<string, string> { { "Content-Type", value } };
        AssertProblem(await handler.HandleAsync(request, context), 400, AuthenticationRoute.RequestCode);
        Assert.That(requests, Is.Zero);
    }
    [TestCase("Content-Type", "application/json")]
    [TestCase("content-type", "application/json; charset=utf-8")]
    [TestCase("CONTENT-TYPE", "Application/JSON; Charset=\"UTF-8\"")]
    public async Task JsonHeaderNamesAndUtf8CharsetAreCaseInsensitive(string name, string value)
    {
        using var handler = Handler();
        var request = Request();
        request.Headers = new Dictionary<string, string> { { name, value } };
        AssertResponse(await handler.HandleAsync(request, context), 202);
    }
    [Test]
    public async Task MultiValueContentTypeAcceptsMirrorButRejectsConflict()
    {
        using var handler = Handler();
        var request = Request();
        request.MultiValueHeaders = new Dictionary<string, IList<string>> { { "content-type", new List<string> { "application/json" } } };
        AssertResponse(await handler.HandleAsync(request, context), 202);
        request.Headers = null;
        AssertResponse(await handler.HandleAsync(request, context), 202);
        request.Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } };
        AssertProblem(await handler.HandleAsync(request, context), 400, AuthenticationRoute.RequestCode);
        Assert.That(requests, Is.EqualTo(2));
    }
    [TestCase("")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("{\"document\":null}")]
    [TestCase("{\"document\":42}")]
    [TestCase("{\"document\":\"52998224725\",\"role\":\"Admin\"}")]
    [TestCase("{\"document\":\"52998224725\",\"Document\":\"52998224725\"}")]
    [TestCase("{\"document\":\"52998224725\",}")]
    [TestCase("{\"document\":\"\\ud800\"}")]
    [TestCase("{\"document\":\"invalid\"}")]
    public async Task InvalidBodyOrDocumentDoesNotInitialize(string body)
    {
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(Request(body: body), context), 400, AuthenticationRoute.RequestCode);
        Assert.That(requests, Is.Zero);
    }
    [TestCase("challengeId")]
    [TestCase("code")]
    [TestCase("document")]
    public async Task VerificationRequiresEveryField(string missing)
    {
        using var handler = Handler();
        var body = new Dictionary<string, object> { { "document", Document }, { "challengeId", Challenge }, { "code", "01234567" } };
        body.Remove(missing);
        AssertProblem(await handler.HandleAsync(Request(AuthenticationRoute.VerifyCode, JsonSerializer.Serialize(body)), context), 400, AuthenticationRoute.VerifyCode);
        Assert.That(verifications, Is.Zero);
    }
    [TestCase("00000000-0000-0000-0000-000000000000", "01234567")]
    [TestCase("invalid", "01234567")]
    [TestCase("11111111-2222-4333-8444-555555555555", "1234567")]
    [TestCase("11111111-2222-4333-8444-555555555555", "１２３４５６７８")]
    public async Task MalformedChallengeOrCodeDoesNotExecute(string id, string code)
    {
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(Request(AuthenticationRoute.VerifyCode, JsonSerializer.Serialize(new { document = Document, challengeId = id, code })), context), 400, AuthenticationRoute.VerifyCode);
        Assert.That(verifications, Is.Zero);
    }
    [Test]
    public async Task ParserAcceptsPropertyCasingAndNormalizesDocument()
    {
        using var handler = Handler();
        AssertResponse(await handler.HandleAsync(Request(body: "{\"DOCUMENT\":\"52998224725\"}"), context), 202);
        Assert.That(observedDocument, Is.EqualTo("529.982.247-25"));
    }
    [Test]
    public async Task Base64IsDecodedWithTheSameLimit()
    {
        using var handler = Handler();
        var request = Request();
        request.IsBase64Encoded = true;
        request.Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body));
        AssertResponse(await handler.HandleAsync(request, context), 202);
    }
    [TestCase("not%base64")]
    [TestCase("/w==")]
    public async Task InvalidBase64OrUtf8IsRejected(string body)
    {
        using var handler = Handler();
        var request = Request(body: body);
        request.IsBase64Encoded = true;
        AssertProblem(await handler.HandleAsync(request, context), 400, AuthenticationRoute.RequestCode);
        Assert.That(requests, Is.Zero);
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task BodyIsLimitedToFourKiB(bool base64)
    {
        using var handler = Handler();
        var request = Request();
        request.Body = request.Body.PadRight(4096, ' ');
        request.IsBase64Encoded = base64;
        if (base64) request.Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body));
        AssertResponse(await handler.HandleAsync(request, context), 202);
        request.Body = base64 ? Convert.ToBase64String(new byte[4097]) : new string(' ', 4097);
        AssertProblem(await handler.HandleAsync(request, context), 400, AuthenticationRoute.RequestCode);
        Assert.That(requests, Is.EqualTo(1));
    }
    [Test]
    public async Task RateLimitReturnsRetryAfterAndFixedProblem()
    {
        using var handler = Handler(request: CustomerCodeRequestResponse.RateLimited(60));
        var response = await handler.HandleAsync(Request(), context);
        AssertProblem(response, 429, AuthenticationRoute.RequestCode);
        Assert.That(response.Headers["Retry-After"], Is.EqualTo("60"));
        Assert.That(Log(), Does.Contain("rate-limited"));
    }
    [Test]
    public async Task DeniedVerificationRemainsGenericAndAuthorizationCannotBypassIt()
    {
        using var handler = Handler(CustomerAuthenticationResult.NotEligible());
        var request = Request(AuthenticationRoute.VerifyCode);
        request.Headers["Authorization"] = "Bearer sensitive-token";
        AssertProblem(await handler.HandleAsync(request, context), 401, AuthenticationRoute.VerifyCode);
        Assert.That(verifications, Is.EqualTo(1));
        Assert.That(Log(), Does.Not.Contain("sensitive-token"));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task TechnicalErrorsAndTimeoutAreSanitized(bool timeout)
    {
        using var handler = new FunctionHandler((_, _) => Task.FromException<CustomerCodeRequestResponse>(timeout ? new TimeoutException("sensitive") : new InvalidOperationException("sensitive")), (_, _, _, _) => throw new AssertionException("Verification must not run in this scenario."));
        var response = await handler.HandleAsync(Request(), context);
        AssertProblem(response, 500, AuthenticationRoute.RequestCode);
        Assert.That(response.Body + Log(), Does.Not.Contain("sensitive"));
        Assert.That(Log(), Does.Contain(timeout ? "timeout" : "technical-failure"));
    }
    [Test]
    public async Task DeadlineCancelsTheOperationAndPreservesResponseReserve()
    {
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FunctionHandler(async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); return CustomerCodeRequestResponse.Accepted(Challenge); } finally { canceled.TrySetResult(token.IsCancellationRequested); } }, (_, _, _, _) => throw new AssertionException("Verification must not run in this scenario."));
        context.RemainingTime.Returns(TimeSpan.FromSeconds(1.1));
        AssertProblem(await handler.HandleAsync(Request(), context), 500, AuthenticationRoute.RequestCode);
        Assert.That(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
    }
    [Test]
    public async Task ExhaustedBudgetDoesNotStartExecution()
    {
        using var handler = Handler();
        context.RemainingTime.Returns(TimeSpan.FromSeconds(1));
        AssertProblem(await handler.HandleAsync(Request(), context), 500, AuthenticationRoute.RequestCode);
        Assert.That(requests, Is.Zero);
    }
    [Test]
    public async Task LambdaSerializerKeepsRestProxyAndJsonStringToken()
    {
        var serializer = new DefaultLambdaJsonSerializer();
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(Request(AuthenticationRoute.VerifyCode)));
        var request = serializer.Deserialize<APIGatewayProxyRequest>(input);
        using var handler = Handler();
        var response = await handler.HandleAsync(request, context);
        using var output = new MemoryStream();
        serializer.Serialize(response, output);
        using var json = JsonDocument.Parse(output.ToArray());
        Assert.That(json.RootElement.GetProperty("statusCode").GetInt32(), Is.EqualTo(200));
        Assert.That(JsonSerializer.Deserialize<string>(json.RootElement.GetProperty("body").GetString()!), Is.EqualTo("test.jwt.token"));
    }
}
