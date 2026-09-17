# GerenciamentoMecanicaAutenticacao

Repositório da função AWS Lambda de validação de CPF e emissão de JWT do sistema de oficina — Tech Challenge Fase 3.

## Estado atual

Estrutura inicial da E1.2. Não havia uma função no repositório original a transferir; sua implementação será feita na E3, com .NET 10 e empacotamento ZIP. O login de usuários internos continua na aplicação. Este repositório ainda não contém função executável, Terraform ou pipeline.

O endpoint planejado é `POST /customers/validate`, encaminhado pelo API Gateway à função. Ela validará CPF/cadastro Ativo no Aurora e emitirá JWT compatível com a API. As regras comuns serão consumidas pelo pacote `GerenciamentoMecanica.Auth.Contracts`, cuja extração pertence à E1.9. Não copiar entidades de OS, controllers ou acesso completo ao banco para este repositório.

## Referências

- [Aplicação, contratos de acesso e plano central](https://github.com/pknfelps/GerenciamentoMecanicaSistema)
- [Infraestrutura](https://github.com/pknfelps/GerenciamentoMecanicaInfraestrutura)
- [Banco](https://github.com/pknfelps/GerenciamentoMecanicaBancoDados)

O contrato completo está em `docs/arquitetura/ACESSO_E_AUTENTICACAO.md` na aplicação; consultar a branch da implementação enquanto o PR não estiver integrado. README de execução/deploy, testes e OpenAPI serão completados conforme E1.3/E3. Não há endpoint publicado nesta etapa.
