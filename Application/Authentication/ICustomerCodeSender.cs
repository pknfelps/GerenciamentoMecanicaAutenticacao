namespace GerenciamentoMecanica.Autenticacao.Application.Authentication;

public interface ICustomerCodeSender
{
    Task SendAsync(string name, string email, string code, CancellationToken cancellationToken);
}
