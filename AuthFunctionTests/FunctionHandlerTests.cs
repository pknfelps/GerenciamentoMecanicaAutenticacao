using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using GerenciamentoMecanica.Autenticacao.Application.Authentication;
using GerenciamentoMecanica.Autenticacao.Domain.Customers;
using GerenciamentoMecanica.Autenticacao.Function;
using NSubstitute;
using System.Text;
using System.Text.Json;

namespace GerenciamentoMecanica.Autenticacao.FunctionTests;

public class FunctionHandlerTests
{
    private const string Document = "529.982.247-25";
    private const string Sensitive = "test-only-password-key-document-token";
    private ILambdaContext context = null!;
    private ILambdaLogger logger = null!;
    private int calls;
    private string? observedDocument;
    private CancellationToken observedCancellation;

    [SetUp]
    public void SetUp()
    {
        calls = 0;
        observedDocument = null;
        observedCancellation = default;
        logger = Substitute.For<ILambdaLogger>();
        context = Substitute.For<ILambdaContext>();
        context.Logger.Returns(logger);
        context.AwsRequestId.Returns("lambda-request-id");
        context.RemainingTime.Returns(TimeSpan.FromSeconds(15));
    }

    private static APIGatewayProxyRequest Request(string? body = null) => new()
    {
        HttpMethod = "POST",
        Path = "/customers/validate",
        Body = body ?? JsonSerializer.Serialize(new { document = Document }),
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { RequestId = "gateway-request-id" }
    };

    private FunctionHandler Handler(CustomerAuthenticationResult? result = null) => new((document, cancellation) =>
    {
        calls++;
        observedDocument = document;
        observedCancellation = cancellation;
        return Task.FromResult(result ?? CustomerAuthenticationResult.Authenticated("test.jwt.token"));
    });

    private string Log() => (string)logger.ReceivedCalls().Single().GetArguments()[0]!;

    private static void AssertResponse(APIGatewayProxyResponse response, int status, string contentType)
    {
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(response.Headers["Content-Type"], Is.EqualTo(contentType));
            Assert.That(response.Headers["Cache-Control"], Is.EqualTo("no-store"));
            Assert.That(response.IsBase64Encoded, Is.False);
        });
    }

    private static void AssertProblem(APIGatewayProxyResponse response, int status)
    {
        AssertResponse(response, status, "application/problem+json");
        using var problem = JsonDocument.Parse(response.Body);
        Assert.Multiple(() =>
        {
            Assert.That(problem.RootElement.GetProperty("type").GetString(), Is.EqualTo("about:blank"));
            Assert.That(problem.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(status));
            Assert.That(problem.RootElement.GetProperty("title").GetString(), Is.Not.Empty);
            Assert.That(problem.RootElement.GetProperty("detail").GetString(), Is.Not.Empty);
            Assert.That(problem.RootElement.GetProperty("instance").GetString(), Is.EqualTo("/customers/validate"));
            Assert.That(problem.RootElement.EnumerateObject().Count(), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task SuccessReturnsTokenAsJsonStringAndLogsOnlySafeFields()
    {
        const string token = "test.jwt.\"token\n";
        using var handler = Handler(CustomerAuthenticationResult.Authenticated(token));
        var response = await handler.HandleAsync(Request(), context);
        AssertResponse(response, 200, "application/json");
        Assert.That(JsonSerializer.Deserialize<string>(response.Body), Is.EqualTo(token));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(observedDocument, Is.EqualTo(Document));
        Assert.That(observedCancellation.CanBeCanceled, Is.True);

        using var log = JsonDocument.Parse(Log());
        Assert.Multiple(() =>
        {
            Assert.That(log.RootElement.GetProperty("lambdaRequestId").GetString(), Is.EqualTo("lambda-request-id"));
            Assert.That(log.RootElement.GetProperty("gatewayRequestId").GetString(), Is.EqualTo("gateway-request-id"));
            Assert.That(log.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(200));
            Assert.That(log.RootElement.GetProperty("durationMs").GetInt64(), Is.GreaterThanOrEqualTo(0));
            Assert.That(log.RootElement.GetProperty("category").GetString(), Is.EqualTo("authenticated"));
            Assert.That(log.RootElement.EnumerateObject().Count(), Is.EqualTo(5));
            Assert.That(Log(), Does.Not.Contain(Document).And.Not.Contain("test.jwt"));
        });
    }

    [TestCase("Content-Type", "application/json")]
    [TestCase("content-type", "application/json; charset=utf-8")]
    [TestCase("CONTENT-TYPE", "Application/JSON; Charset=\"UTF-8\"")]
    public async Task JsonContentTypeIsCaseInsensitive(string name, string value)
    {
        var request = Request();
        request.Headers = new Dictionary<string, string> { [name] = value };
        using var handler = Handler();
        AssertResponse(await handler.HandleAsync(request, context), 200, "application/json");
    }

    [Test]
    public async Task MultiValueHeaderWorksWithOrWithoutSingleValueMirror()
    {
        using var handler = Handler();
        var request = Request();
        request.MultiValueHeaders = new Dictionary<string, IList<string>>
        {
            ["content-type"] = new List<string> { "application/json; charset=utf-8" }
        };
        request.Headers = null;
        AssertResponse(await handler.HandleAsync(request, context), 200, "application/json");
        request.Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
        AssertResponse(await handler.HandleAsync(request, context), 200, "application/json");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("text/plain")]
    [TestCase("application/problem+json")]
    [TestCase("application/json; charset=latin-1")]
    [TestCase("application/json; charset=utf-8; charset=utf-8")]
    [TestCase("application/json; boundary=unexpected")]
    public async Task MissingOrInvalidContentTypeDoesNotExecute(string? value)
    {
        var request = Request();
        request.Headers = value is null ? null : new Dictionary<string, string> { ["Content-Type"] = value };
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(request, context), 400);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task ConflictingHeaderValuesAreRejected()
    {
        var request = Request();
        request.MultiValueHeaders = new Dictionary<string, IList<string>>
        {
            ["Content-Type"] = new List<string> { "application/json", "text/plain" }
        };
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(request, context), 400);
        Assert.That(calls, Is.Zero);
    }

    [TestCase("")]
    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("42")]
    [TestCase("\"document\"")]
    [TestCase("{}")]
    [TestCase("{")]
    [TestCase("{\"document\":null}")]
    [TestCase("{\"document\":42}")]
    [TestCase("{\"document\":true}")]
    [TestCase("{\"document\":[]}")]
    [TestCase("{\"document\":{}}")]
    [TestCase("{\"document\":\"\"}")]
    [TestCase("{\"document\":\" \"}")]
    [TestCase("{\"Document\":\"52998224725\"}")]
    [TestCase("{\"document\":\"52998224725\",\"role\":\"Admin\"}")]
    [TestCase("{\"document\":\"52998224725\",\"document\":\"52998224725\"}")]
    [TestCase("{\"document\":\"52998224725\",\"document\":null}")]
    [TestCase("{\"document\":\"52998224725\",}")]
    [TestCase("{/* comment */\"document\":\"52998224725\"}")]
    [TestCase("{\"document\":\"\\ud800\"}")]
    public async Task InvalidJsonShapeDoesNotExecute(string body)
    {
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(Request(body), context), 400);
        Assert.That(calls, Is.Zero);
        Assert.That(Log(), Does.Not.Contain(body.Length > 10 ? body : Sensitive));
    }

    [Test]
    public async Task MissingBodyDoesNotExecute()
    {
        var request = Request();
        request.Body = null;
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(request, context), 400);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task Base64BodyIsDecoded()
    {
        var request = Request();
        request.Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body));
        request.IsBase64Encoded = true;
        using var handler = Handler();
        AssertResponse(await handler.HandleAsync(request, context), 200, "application/json");
        Assert.That(observedDocument, Is.EqualTo(Document));
    }

    [TestCase("not%base64")]
    [TestCase("/w==")]
    public async Task InvalidBase64OrUtf8DoesNotExecute(string body)
    {
        var request = Request(body);
        request.IsBase64Encoded = true;
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(request, context), 400);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task InvalidDocumentUsesProblemDetails()
    {
        using var handler = Handler(CustomerAuthenticationResult.InvalidDocument());
        AssertProblem(await handler.HandleAsync(Request(), context), 400);
        Assert.That(Log(), Does.Contain("invalid-document"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingAndInactiveCustomerHaveIdenticalDenial(bool inactive)
    {
        var normalizer = Substitute.For<ICustomerDocumentNormalizer>();
        var customers = Substitute.For<ICustomerLookup>();
        var tokens = Substitute.For<ICustomerTokenIssuer>();
        normalizer.Normalize(Document).Returns(Document);
        customers.FindByDocumentAsync(Document, Arg.Any<CancellationToken>())
            .Returns(inactive ? new CustomerIdentity(Guid.NewGuid(), "Test customer", Document, CustomerStatus.Inactive) : null);
        var useCase = new ValidateCustomerDocumentUseCase(normalizer, customers, tokens);
        using var handler = new FunctionHandler(useCase.ExecuteAsync);
        var response = await handler.HandleAsync(Request(), context);
        AssertProblem(response, 401);
        using var problem = JsonDocument.Parse(response.Body);
        Assert.That(problem.RootElement.GetProperty("detail").GetString(), Is.EqualTo("Não foi possível autenticar o cliente."));
        tokens.DidNotReceiveWithAnyArgs().Generate(default!);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TechnicalFailureIsSanitized(bool canceled)
    {
        using var handler = new FunctionHandler((_, _) => Task.FromException<CustomerAuthenticationResult>(
            canceled ? new OperationCanceledException(Sensitive) : new InvalidOperationException(Sensitive)));
        var response = await handler.HandleAsync(Request(), context);
        AssertProblem(response, 500);
        Assert.That(response.Body, Does.Not.Contain(Sensitive));
        Assert.That(Log(), Does.Not.Contain(Sensitive).And.Not.Contain("Exception"));
    }

    [Test]
    public async Task InvocationDeadlineCancelsExecutionAndReturns500()
    {
        context.RemainingTime.Returns(TimeSpan.FromSeconds(1.05));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FunctionHandler(async (_, cancellation) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return CustomerAuthenticationResult.NotEligible();
            }
            finally
            {
                completion.TrySetResult(cancellation.IsCancellationRequested);
            }
        });
        AssertProblem(await handler.HandleAsync(Request(), context), 500);
        Assert.That(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(Log(), Does.Contain("timeout"));
    }

    [Test]
    public async Task ExhaustedBudgetDoesNotExecute()
    {
        context.RemainingTime.Returns(TimeSpan.FromSeconds(1));
        using var handler = Handler();
        AssertProblem(await handler.HandleAsync(Request(), context), 500);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public async Task LateSuccessAfterCancellationIsNotReturnedAsToken()
    {
        using var handler = new FunctionHandler(async (_, cancellation) =>
        {
            await Task.Delay(50);
            return CustomerAuthenticationResult.Authenticated(Sensitive);
        });
        context.RemainingTime.Returns(TimeSpan.FromSeconds(1.02));
        var response = await handler.HandleAsync(Request(), context);
        AssertProblem(response, 500);
        Assert.That(response.Body, Does.Not.Contain(Sensitive));
    }

    [Test]
    public async Task AuthorizationHeaderDoesNotBypassExecution()
    {
        var request = Request();
        request.Headers["Authorization"] = "Bearer " + Sensitive;
        using var handler = Handler(CustomerAuthenticationResult.NotEligible());
        AssertProblem(await handler.HandleAsync(request, context), 401);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(Log(), Does.Not.Contain(Sensitive));
    }

    [Test]
    public async Task LambdaSerializerPreservesRestProxyEnvelopeAndJsonStringBody()
    {
        var serializer = new DefaultLambdaJsonSerializer();
        var eventJson = JsonSerializer.Serialize(new
        {
            httpMethod = "POST", path = "/customers/validate",
            headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            body = "{\"document\":\"52998224725\"}",
            isBase64Encoded = false,
            requestContext = new { requestId = "gateway-request-id" }
        });
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(eventJson));
        var request = serializer.Deserialize<APIGatewayProxyRequest>(input);
        const string token = "test.jwt.\"escaped\"";
        using var handler = Handler(CustomerAuthenticationResult.Authenticated(token));
        var response = await handler.HandleAsync(request, context);
        using var output = new MemoryStream();
        serializer.Serialize(response, output);
        using var json = JsonDocument.Parse(output.ToArray());
        Assert.That(json.RootElement.GetProperty("statusCode").GetInt32(), Is.EqualTo(200));
        Assert.That(JsonSerializer.Deserialize<string>(json.RootElement.GetProperty("body").GetString()!), Is.EqualTo(token));
        Assert.That(json.RootElement.GetProperty("isBase64Encoded").GetBoolean(), Is.False);
        Assert.That(observedDocument, Is.EqualTo("52998224725"));
    }

    [Test]
    public async Task MissingGatewayRequestContextIsSafe()
    {
        var request = Request();
        request.RequestContext = null;
        using var handler = Handler();
        AssertResponse(await handler.HandleAsync(request, context), 200, "application/json");
        using var json = JsonDocument.Parse(Log());
        Assert.That(json.RootElement.GetProperty("gatewayRequestId").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }
}
