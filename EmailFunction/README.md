# EmailFunction — transporte SMTP centralizado

Recebe o contrato Invoke v1 de código de acesso, orçamento ou mudança de status; valida o envelope e transmite uma mensagem ao SMTP4dev. Não consulta banco, emite JWT, monta conteúdo de negócio ou oferece endpoint público.

Handler: `GerenciamentoMecanica.Serverless.EmailFunction::GerenciamentoMecanica.Serverless.EmailFunction.FunctionHandler::HandleAsync`.

A resposta `captured` confirma a aceitação SMTP do DATA, sem afirmar entrega externa ou persistência. Erros tratados usam `invalid-request`, `smtp-unavailable`, `timeout` ou `technical-failure`, sem detalhes internos. Configuração/transporte reutilizados; conexão SMTP própria por invocação, sem autenticação, TLS, retry ou relay externo. Orçamento compartilhado de até três segundos, limitado pelo tempo restante menos um segundo para resposta.

| Variável | Valor |
|---|---|
| Email__SmtpHost | Obrigatória, hostname/IP sem esquema ou porta |
| Email__SmtpPort | 25 por padrão; AWS recebe 2525 do catálogo da base |
| Email__SenderName | Mecânica por padrão |
| Email__SenderEmail | noreply@mecanica.local por padrão |
| Runtime__Environment | Ausente localmente ou hom/prd; nesses ambientes exige AWS_REGION=us-east-1 |

O payload não escolhe remetente, conexão, credenciais ou anexos. Um JSON técnico de conclusão por invocação, sem destinatário, código, corpo, configuração ou exceção completa. Sob o formato JSON nativo da Lambda, o JSON da aplicação aparece no campo `message` do registro da plataforma.

[Contrato Invoke](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/CONTRATOS_AUTENTICACAO_E_EMAIL.md) · [Execução local](../README.md#emailfunction-local) · [SAM privado](../templates/email.yaml).
