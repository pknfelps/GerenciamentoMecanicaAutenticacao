using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using GerenciamentoMecanica.Serverless.EmailFunction.Configuration;
using GerenciamentoMecanica.Serverless.EmailFunction.Contracts;
using GerenciamentoMecanica.Serverless.EmailFunction.Smtp;
using MailKit;
using MailKit.Net.Smtp;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace GerenciamentoMecanica.Serverless.EmailFunction;

public sealed class FunctionHandler
{
    private static readonly TimeSpan MaximumBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResponseReserve = TimeSpan.FromSeconds(1);
    private readonly Lazy<ISmtpEmailSender> sender;

    public FunctionHandler() : this(() => new SmtpEmailSender(SmtpSettings.Load()))
    {
    }

    public FunctionHandler(Func<ISmtpEmailSender> senderFactory)
    {
        ArgumentNullException.ThrowIfNull(senderFactory);
        // Configuracao validada uma vez ao inicializar o transporte; falhas ficam dentro do boundary sanitizado.
        sender = new Lazy<ISmtpEmailSender>(senderFactory);
    }

    [LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]
    public async Task<EmailResponse> HandleAsync(Stream input, ILambdaContext context)
    {
        var elapsed = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        EmailRequest? request = null;
        EmailResponse response;
        string? exceptionType = null;

        try
        {
            var budget = context.RemainingTime - ResponseReserve;
            if (budget <= TimeSpan.Zero)
                throw new OperationCanceledException();
            cancellation.CancelAfter(budget < MaximumBudget ? budget : MaximumBudget);

            request = await EmailRequestParser.ParseAsync(input, cancellation.Token).WaitAsync(cancellation.Token);
            if (request is null)
                response = EmailResponse.Failure(EmailFailure.InvalidRequest);
            else
            {
                await sender.Value.SendAsync(request, cancellation.Token).WaitAsync(cancellation.Token);
                response = EmailResponse.Captured();
            }
        }
        catch (Exception error)
        {
            exceptionType = error.GetType().Name;
            var failure = Classify(error, cancellation.IsCancellationRequested, request is not null);
            response = EmailResponse.Failure(failure);
        }

        context.Logger.LogLine(JsonSerializer.Serialize(new
        {
            message = "Processamento de envio de e-mail concluído.",
            lambdaRequestId = context.AwsRequestId,
            type = request?.Type,
            outcome = response.Outcome,
            error = response.Error,
            durationMs = elapsed.ElapsedMilliseconds,
            exceptionType
        }));
        return response;
    }

    private static EmailFailure Classify(Exception error, bool deadlineExpired, bool requestWasValid)
    {
        if (deadlineExpired || error is OperationCanceledException or TimeoutException)
            return EmailFailure.Timeout;
        if (requestWasValid && error is IOException or SocketException or SmtpCommandException or SmtpProtocolException or ServiceNotConnectedException)
            return EmailFailure.SmtpUnavailable;
        return EmailFailure.TechnicalFailure;
    }
}
