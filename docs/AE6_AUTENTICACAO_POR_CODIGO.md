# AE.6 — Autenticação por código e integração do envio

Implementação preparada e validada localmente em 22/10/2026. Integra o store AE.3 ao transporte AE.5. Nenhum login/deploy, recurso AWS, IAM, SQL, workflow ou Gateway foi alterado nesta etapa. Os aceites oficiais E5/E6/E7 permanecem separados.

## Contrato e fluxo

[OpenAPI 2.0.0](../contracts/openapi.json) e [contrato central AE.1](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/CONTRATOS_EXTRA_AUTENTICACAO_E_EMAIL.md).

| Rota | Entrada | Sucesso |
|---|---|---|
| POST /customers/auth/request-code | document | 202 com challengeId opaco e mensagem fixa |
| POST /customers/auth/verify-code | challengeId, code de oito dígitos ASCII e document | 200 com string JSON JWT Customer |

JSON estrito de até 4 KiB UTF-8, inclusive após base64; comparação de propriedades sem distinção de maiúsculas, sem extras/duplicações/null/tipos incorretos. CPF/CNPJ e estrutura são validados antes do bootstrap AWS/SQL. Authorization não seleciona o cliente nem evita o código. Todas as respostas têm no-store; erros usam problem+json com mensagens fixas e instance da rota reconhecida. Rota antiga/desconhecida retorna 404 fixo na invocação direta; método diferente de POST retorna 405/Allow: POST.

```mermaid
sequenceDiagram
    participant C as Cliente/Postman
    participant A as AuthFunction
    participant D as PostgreSQL
    participant E as EmailFunction
    participant S as SMTP4dev privado
    C->>A: request-code(document)
    A->>D: Localizar cliente elegível; criar Pending e confirmar commit
    A->>E: Invoke v1 síncrono sem logs/retry
    E->>S: Captura SMTP
    E-->>A: captured válido
    A->>D: Ativar Ready condicionalmente
    A-->>C: 202 com challengeId, sem código
    C->>S: Operador consulta captura privada
    C->>A: verify-code(challengeId, code, document)
    A->>D: Verificar e consumir atomicamente
    D-->>A: Commit confirmado
    A-->>C: 200 com JWT Customer
```

RequestCustomerCodeUseCase não mantém transação durante Invoke. Cadastro ausente/Inactive/sem e-mail utilizável retorna 202 genérico sem código, escrita, limpeza ou envio. O store registra tentativa de cliente não encontrado com mensagem fixa. Cooldown/limite retornam 429 com Retry-After; solicitações admitidas contam mesmo se envio falhar.

Captura é reconhecida somente com HTTP 200 do Invoke, FunctionError ausente e schema exato version=1/outcome=captured. Falha/timeout/resposta inválida invalida Pending quando possível, com até um segundo vinculado ao prazo do chamador; falha de invalidação não substitui a original. Pending nunca autentica e expira. Ativação false por substituição/expiração mantém o 202 sem reativação. Uma mensagem capturada antes de timeout pode conter código inutilizável; não há entrega única/reenvio automático.

VerifyCustomerCodeUseCase delega ao consumo transacional já validado: negativos iguais em 401; tentativa incorreta/documento divergente é persistido; quinta tentativa invalida. Estados Pending/expirado/Consumed/Invalidated e cliente ausente/Inactive não autenticam. A assinatura é preparada antes do consumo, e o token só retorna após commit. Duas confirmações não retornam dois tokens. Falha técnica/commit ambíguo não é convertido em negativa de negócio.

JWT mantém HS256, issuer/audience por ambiente, UUID/nome cadastrados, role Customer e dez minutos, sem documento, e-mail, código, challengeId ou OS. Não há refresh/replay. O caso de uso de emissão por documento isolado foi removido. A validação Active/ClockSkew zero e a decisão de orçamento da API seguem na AE.7.

## Composição, AWS e prazo

FunctionRuntimeProvider inicializa uma vez, reutiliza os casos de uso/data source/cliente Invoke, permite nova tentativa após bootstrap malsucedido e descarta runtime abandonado após cancelamento. Disposição do handler fecha os recursos. PostgreSQL continua sem pooling, com TLS VerifyFull/certificado e limites atuais; o store AE.3 e o esquema não mudaram.

O loader mantém o orçamento global de oito segundos e as duas leituras Secrets Manager. SSM agora usa dois lotes: nove parâmetros base/database e dois email/v1 (invocation-arn e contract-version). Cada chamada lógica produz a medição existente; bootstrap bem-sucedido passa a registrar duas operações SSM e duas Secrets Manager. Não consultar SSM por mensagem.

Somente `arn:aws:lambda:us-east-1:121754142617:function:mecanica-<ambiente>-email:live` e contrato 1 são aceitos. AWSSDK.Lambda 4.0.108.1 está fixado na Infrastructure. O cliente usa região fixa, MaxErrorRetry=0, LogResponse=false, LogMetrics=false, timeout de cinco segundos e IgnoreConfiguredEndpointUrls=true. Auth não ganha configuração local alternativa de JWT/banco/endpoint: testes injetam bootstrap, sender e configuração.

Invoke usa RequestResponse/LogType=None, corpo MIME produzido exclusivamente na EmailFunction e nenhum LogResult solicitado. A origem monta texto/HTML com encoding do nome no HTML. Não propagar resposta/payload original da função ou do SDK em exceções/logs.

O handler preserva RemainingTime menos um segundo. Esse CancellationToken limita bootstrap, SQL, envio e ativação; Invoke também tem teto de cinco segundos. Não iniciar envio sem margem/cancelamento válido. O flush existente permanece compartilhado de até 500 ms, com reserva de resposta de 500 ms. Nenhuma dependência recebe flush próprio ou retry adicional.

## Observabilidade

Manifesto mantém meters, dimensões, providers, exportação e propagação somente W3C. Os dois nomes de spans são declarados em InvocationSpanNames; a rota vem de uma seleção fixa, com fallback /customers/auth, sem caminho recebido nos nomes. Relação invocação → PostgreSQL e logs correlacionados é preservada; sem novos spans manuais ou dimensões de identidade.

Categorias acrescentadas: code-requested e rate-limited. Permanecem authenticated, invalid-request, invalid-document, not-eligible, timeout e technical-failure. 400/401/429 são respostas esperadas, sem erro técnico no span; 500/timeouts são erros. A métrica de invocações inclui solicitações e confirmações; seu total **não é quantidade de logins**. Filtrar outcome=authenticated para autenticações efetivas. Duração continua terminando antes do flush.

Uma conclusão JSON por invocação preserva os campos técnicos atuais. Diagnósticos fixos do store (cliente não encontrado, limpeza ou invalidação) vão ao LambdaLogger e, quando habilitado, ao provider OTLP existente; sem documento, código, destinatário, challengeId, SQL, mensagem original/stack ou configuração. Esses diagnósticos não criam outro provider de console.

## Conferências locais

- Restore/build Release da solução sem avisos/erros.
- **438 testes aprovados, zero falhas/ignorados**: 231 Infrastructure, 91 EmailFunction, 74 AuthFunction, 32 Application e 10 Contract. Relatório ignorado: TestResults/ae6-regression.trx.
- Testes HTTP de JSON/base64/UTF-8/4 KiB, casing, campos, remoção da rota antiga, 404/405, 202/200/400/401/429/500, no-store, categorias e sanitização.
- Casos de uso: ordem persistir → capturar → ativar, substituição, rate limit, falha de envio/ativação/invalidação, cancelamento e consumo delegado.
- Invoke: schema, status, FunctionError, duplicações/campos extras, erro remoto, timeout/cancelamento, encoding HTML, configuração sem retries/logging e isolamento hom/prd.
- Fixture PostgreSQL existente: solicitação sem transação aberta durante envio, Ready após captura, cooldown, consumo concorrente único, claims JWT, negativa sem escrita/envio, invalidação sem reembolso, falha SQL, fechamento de conexões canceladas, bootstrap reutilizado/nova tentativa/cancelamento e descarte.
- Fluxo completo de handler: request-code → adaptador Invoke → handler real EmailFunction → SMTP4dev 3.15.0 em container → leitura MIME → verify-code → JWT. AWS Invoke é substituído no teste; PostgreSQL, MIME e SMTP são reais/descartáveis. Não afirmar IAM/VPC/runtime AWS a partir desse teste. Não foram usados dados/credenciais reais; código/JWT não são registrados pelos handlers.
- Testes anteriores de regras, PBKDF2/expiração/tentativas/locks/rollback/limpeza e transporte SMTP preservados/adaptados; testes de emissão por documento foram substituídos pelo novo fluxo.
- SAM validate --lint/build dos dois templates e publish linux-x64/framework-dependent conferidos. Pacote auth contém SDK Lambda, certificado RDS original e JSON/YAML OTel; sem EmailFunction/MailKit ou dependências de teste. Pacote email permanece isolado, sem banco/JWT.

Docker foi usado sem inicialização automática. SDK 10.0.402 compilou; os testes executaram no runtime/VSTest instalados, conforme a limitação da cópia temporária do SDK já registrada na AE.5. Nenhum teste indisponível foi ignorado. Recursos descartáveis encerrados.

## Execução e sequência posterior

Na raiz, com Docker ativo e pacote compartilhado já disponível, executar restore/build/test da solução pelos comandos do README. Para focalizar o fluxo com captura sem criar executável/script:

```powershell
dotnet test InfrastructureTests/InfrastructureTests.csproj -c Release --filter FullyQualifiedName~RequestVerifyFlowUsesTheRealEmailHandlerAndSmtpCapture
```

Os testes do handler usam bootstrap/configuração AWS injetados e PostgreSQL local. Não usar SAM da autenticação contra banco/JWT alternativos para contornar as exigências AWS. [Procedimento da EmailFunction isolada no SAM](AE5_EMAIL_FUNCTION.md).

**Próximo passo: AE.7**, conectar a API ao Invoke e aplicar Active/ClockSkew/decisão explícita do orçamento. AE.8 integra workflows, publica os dois caminhos no Gateway e remove a rota antiga/contrato legado coordenadamente. Não publicar esta revisão da auth no Gateway antigo isoladamente: o handler já responde 404 para customers/validate. AE.9 comprova o conjunto; AE.10 implanta após autorização, com bootstrap/base/banco/email antes dos consumidores. Nenhum plano AWS ou aplicação está implícito nesta conclusão local.

O dashboard existente de integrações recebeu somente orientação sobre a população e limite de oito facetas de resultado. Terraform fmt/init sem backend/validate e JSON renderizado aprovados, sem plan/apply ou consultas ao New Relic. Demais dashboards e cinco alertas preservados.
