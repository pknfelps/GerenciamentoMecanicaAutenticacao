# EmailFunction — estrutura AE.2

Projeto independente para o transporte SMTP centralizado. AE.2 cria somente a estrutura da biblioteca e do projeto EmailFunctionTests; handler, contrato executável, MailKit e testes funcionais pertencem à AE.5. Não há envio, handler fictício ou função publicável nesta etapa.

Sem referências a Domain/Application/Infrastructure da autenticação, Npgsql ou JWT. Não acessar o banco ou emitir tokens. A integração v1 definida na [AE.1](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/CONTRATOS_EXTRA_AUTENTICACAO_E_EMAIL.md) será implementada antes do template/stack/workflows de envio. O projeto de testes ainda não contém casos; nenhum teste artificial foi criado apenas para a estrutura.
