using MimeKit;

namespace GerenciamentoMecanica.Serverless.EmailFunction.Validation;

internal static class MailboxValidation
{
    public static bool IsSafeHeader(string value, int maximumLength) =>
        value.Length <= maximumLength &&
        !value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029');

    public static bool IsAddress(string value) =>
        IsSafeHeader(value, 254) &&
        MailboxAddress.TryParse(new ParserOptions
        {
            AddressParserComplianceMode = RfcComplianceMode.Strict,
            AllowAddressesWithoutDomain = false
        }, value, out var mailbox) &&
        string.IsNullOrEmpty(mailbox.Name) && mailbox.Route.Count == 0 &&
        string.Equals(mailbox.Address, value, StringComparison.Ordinal);
}
