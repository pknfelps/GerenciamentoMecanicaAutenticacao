# Autenticação de Clientes do Sistema de Gerenciamento de Mecânica

Repositório da futura função AWS Lambda que valida o CPF de um cliente e emite um JWT para acesso às suas ordens de serviço. O login com senha dos usuários internos permanece na API da oficina.

## Estado da implementação

Este repositório contém documentação, configuração do Git e um workflow manual de diagnóstico OIDC. **Ainda não há função executável, projeto .NET, testes da função, Terraform, CI de build ou endpoint publicado.** As instruções de build, execução e deploy serão adicionadas junto dos respectivos artefatos.

A tecnologia definida é .NET 10 em AWS Lambda, empacotada como ZIP, com Aurora PostgreSQL e JWT compatível com a API. O contrato compartilhado `GerenciamentoMecanica.Auth.Contracts` ainda será extraído da aplicação e publicado como pacote NuGet.

## Responsabilidades e limites

A função deverá normalizar/validar CPF, consultar diretamente o cliente e seu status com credencial de leitura limitada, verificar elegibilidade e emitir JWT de role Customer. Conexões serão encerradas ao fim da operação, sem pooling, conforme a arquitetura aceita.

Não haverá login do cliente com senha, refresh token ou gerenciamento de OS nesta função. Criação de OS continua exclusiva da oficina. A API valida assinatura, role e propriedade das ordens ao receber o token. Segredos não vêm do corpo da requisição.

## Fluxo planejado

```mermaid
sequenceDiagram
    participant C as Cliente
    participant G as API Gateway REST
    participant L as Lambda .NET
    participant B as Aurora PostgreSQL
    participant A as API da oficina
    C->>G: POST /customers/validate (cpf)
    G->>L: Encaminhar requisição
    L->>B: Consultar cliente e status
    B-->>L: Cadastro elegível
    L-->>G: 200 com JWT Customer
    G-->>C: Token
    C->>G: Consultar OS com Bearer token
    G->>A: Encaminhar via VPC Link/NLB
    A->>A: Validar JWT e propriedade da OS
    A-->>G: Resposta autorizada
    G-->>C: Resultado da consulta
```

O diagrama não representa uma implantação existente. Casos de CPF inválido e cliente inelegível seguem o contrato abaixo.

## Contrato HTTP planejado

`POST /customers/validate`, encaminhado pelo API Gateway à Lambda, com `Content-Type: application/json`:

```json
{
  "cpf": "529.982.247-25"
}
```

| Código | Resultado |
|---|---|
| 200 | Token no corpo, sem envelope de sessão/refresh token |
| 400 | Corpo ou CPF inválido |
| 401 | CPF válido sem cliente elegível: inexistente ou Inativo |
| 5xx | Falha técnica inesperada da função ou integração; não converter em 401 |

A fonte completa é o [contrato de acesso e autenticação](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/ACESSO_E_AUTENTICACAO.md). A função usará HS256, expiração de dez minutos, issuer/audience/chave compatíveis com a API do mesmo ambiente e claims obtidas do cadastro. Configuração secreta ficará no Secrets Manager.

A consulta depende de campos/modelo que ainda serão implementados no repositório de banco. O SQL atual não possui o status de cliente necessário para esse fluxo.

## Pré-requisitos e execução

Para consultar a documentação, basta Git. Para a futura implementação serão necessários .NET SDK 10, pacote de contratos em versão fixa e configuração de acesso ao PostgreSQL/JWT. Testes automatizados e mecanismo de execução local serão definidos junto do projeto.

Não há comando `dotnet build`, teste ou invocação local aplicável neste checkout. O login interno existente pode ser exercitado em `POST /authentication` na [API da oficina](https://github.com/pknfelps/GerenciamentoMecanicaSistema/tree/develop).

## CI e deploy planejados

O workflow deverá compilar, testar e empacotar um ZIP versionado; Terraform/IAM da função pertencerão a este repositório. A entrega AWS usará OIDC e dependerá de rede privada, banco com esquema, credenciais de leitura e segredo JWT. O contrato OpenAPI será publicado junto da versão da função e consumido pela infraestrutura para compor o Gateway.

A observabilidade seguirá OpenTelemetry com extensão Collector local na Lambda e exportação ao New Relic. Versões/compatibilidade e testes de correlação ainda serão verificados. Existe somente o diagnóstico manual OIDC; CI da função e deploy ainda serão implementados.

## Contratos de integração

A [especificação central](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/CONTRATOS_ENTRE_REPOSITORIOS.md) define a interface do produtor **auth**. O [contrato HTTP/JWT](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/ACESSO_E_AUTENTICACAO.md) permanece a fonte de payloads, códigos e claims. A função/publicadores/deploy ainda serão implementados.

| Interface | Responsabilidade da autenticação |
|---|---|
| Consome no build | NuGet GerenciamentoMecanica.Auth.Contracts em versão exata e checksum; baixar do S3 para feed local antes do restore |
| Consome da base | VPC/subnets privadas/SG, JWT por ARN, issuer/audience e configuração New Relic quando habilitada |
| Consome do banco | Endpoint/porta/database/TLS, schema-version/hash e auth-secret-arn |
| Resolve no runtime | Credencial de leitura de clientes, chave JWT e ingestão New Relic; não usa a credencial de escrita da API |
| Publica artefatos | lambda/<commit>/function.zip e contracts/auth/<commit>/openapi.json, cada um com .sha256 |
| Publica em SSM | /mecanica/<ambiente>/auth/v1/: função/versão/ARN qualificado de invocação, ZIP, OpenAPI, versão do pacote, release e tentativas |
| Solicita após deploy | Atualização do Gateway via workflow da infraestrutura fixado por SHA; mantém a release da API já implantada |

O build não depende de checkout local da API nem de Aurora ativo; depende da versão do pacote já publicada. A publicação do pacote pela API também não depende desta função, evitando ciclo de bootstrap.

Ready exige atualização Lambda concluída, versão numérica publicada, conectividade e testes de contrato aprovados. Gateway consome o ARN qualificado dessa versão, não $LATEST, e é dono da permissão de invocação. Não é necessário ter Gateway disponível para publicar a primeira release da função.

O [diagnóstico manual OIDC](.github/workflows/aws-oidc-check.yml) testa a role auth do Environment em develop/hom ou main/prd. Entradas: AWS_REGION, AWS_ROLE_ARN, TF_STATE_BUCKET e ARTIFACTS_BUCKET. Ele comprova autenticação, não substitui CI de .NET, testes funcionais ou validação das permissões de deploy/secrets.

## Desenvolvimento e ambientes

Todo trabalho parte da `develop` atualizada, com branch de tarefa e PR para `develop`. A promoção `develop -> main` ocorrerá ao concluir a entrega. **hom** e **prd** terão funções e configurações próprias, compatíveis com API e banco do mesmo ambiente; essa separação ainda será provisionada.

## Referências

- [Arquitetura de execução](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/rfcs/001-EXECUCAO.md).
- [Entrega e contratos entre componentes](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/rfcs/002-ENTREGA.md).
- [Dados e observabilidade](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/rfcs/003-DADOS-E-OBSERVABILIDADE.md).
- [Infraestrutura](https://github.com/pknfelps/GerenciamentoMecanicaInfraestrutura/tree/develop).
- [Banco](https://github.com/pknfelps/GerenciamentoMecanicaBancoDados/tree/develop).
