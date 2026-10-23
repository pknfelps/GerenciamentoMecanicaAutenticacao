namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public sealed class RequestCustomerCodeUseCase(ICustomerDocumentNormalizer normalizer,
    ICustomerAccessStore store, ICustomerCodeSender sender, Action<string>? invalidationDiagnostic = null)
{
    public async Task<CustomerCodeRequestResponse> ExecuteAsync(string? document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = normalizer.Normalize(document);
        if (canonical is null)
            return CustomerCodeRequestResponse.Invalid();
        var pending = await store.CreatePendingAsync(canonical, cancellationToken);
        if (pending.Outcome == CustomerCodeRequestOutcome.RateLimited)
            return CustomerCodeRequestResponse.RateLimited(pending.RetryAfterSeconds);
        if (pending.Outcome != CustomerCodeRequestOutcome.Pending)
            return CustomerCodeRequestResponse.Accepted(pending.ChallengeId);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sender.SendAsync(pending.Customer!.Name, pending.Email!, pending.Code!, cancellationToken);
            // False means expired/replaced/ineligible; preserve the generic response without reactivation.
            await store.ActivateAsync(pending.Customer.Id, pending.ChallengeId, cancellationToken);
            return CustomerCodeRequestResponse.Accepted(pending.ChallengeId);
        }
        catch
        {
            // Pending is never usable. Best effort invalidation must not mask the original failure.
            if (!cancellationToken.IsCancellationRequested)
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cleanup.CancelAfter(TimeSpan.FromSeconds(1));
                try { await store.InvalidateAsync(pending.Customer!.Id, pending.ChallengeId, cleanup.Token).WaitAsync(cleanup.Token); }
                catch (Exception error)
                {
                    try { invalidationDiagnostic?.Invoke(error.GetType().Name); }
                    catch { /* Diagnostics cannot replace the original failure. */ }
                }
            }
            throw;
        }
    }
}
