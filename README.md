# Funções serverless da oficina

AuthFunction .NET 10 autentica por documento e código de uso único, com desafio PostgreSQL e envio por Invoke à EmailFunction. EmailFunction concentra SMTP/MIME e captura no SMTP4dev, sem relay externo. Login com senha de usuários internos permanece na [API](https://github.com/pknfelps/GerenciamentoMecanicaSistema/tree/develop).

## Estrutura e arquitetura

| Projeto | Responsabilidade |
|---|---|
| Domain | Identidade, status e elegibilidade; sem AWS/banco |
| Application | Caso de uso e interfaces de consulta/emissão |
| Infrastructure | Configuração AWS, PostgreSQL, documentos/JWT e métricas de acessos |
| AuthFunction | Handler REST proxy, bootstrap/runtime reutilizável e composição OTel |
| EmailFunction | Contrato Invoke privado e transporte SMTP centralizado; sem banco/JWT |
| EmailFunctionTests | Contrato, configuração, handler e captura SMTP4dev descartável |
| ApplicationTests/ContractTests/InfrastructureTests/AuthFunctionTests | Regras, pacote, configuração/adaptadores/PostgreSQL e contrato do handler |

Projetos net10.0, Npgsql 10.0.2/Dapper e pacote Auth.Contracts [1.0.0]. SAM CLI 1.167.0/CloudFormation entrega ZIP linux-x64/framework-dependent, runtime dotnet10/x86_64; auth usa 512 MB/15 s e email 512 MB/6 s. Não há Dockerfile da função nem novo executável local; [Compose](compose.observability.yaml) inicia somente Collector para conferência manual.

## Contrato HTTP/JWT

[contracts/openapi.json](contracts/openapi.json), versão 2.0.0, substitui POST /customers/validate por:

| Rota | Entrada | Sucesso |
|---|---|---|
| POST /customers/auth/request-code | document | 202 com challengeId opaco e mensagem genérica, sem código |
| POST /customers/auth/verify-code | challengeId, code, document | 200 com string JSON JWT Customer |

JSON UTF-8 estrito de até 4 KiB, inclusive base64, sem extras/duplicações/null. Propriedades comparadas sem distinção de maiúsculas. Documento normalizado pelo pacote antes do bootstrap; código com oito dígitos ASCII e UUID não vazio. Entradas inválidas não inicializam AWS ou SQL. Authorization não evita a verificação. Erros fixos 400/401/429/500 em problem+json; Retry-After no limite; no-store em todas as respostas.

Cliente ausente/Inactive/sem e-mail utilizável recebe 202 sem escrita/limpeza/envio. Cliente elegível persiste Pending antes do Invoke e só vira Ready após captura confirmada. Falha invalida quando possível sem mascarar a original; Pending nunca autentica. Cooldown de 60 s, cinco pedidos/hora, validade de cinco minutos e cinco tentativas; consumo atômico, sem transação aberta durante envio.

JWT HS256 mantém UUID/nome cadastrado, role Customer e dez minutos, sem documento, e-mail, código, challengeId ou OS. Assinatura/consumo só retornam token após commit; sem refresh/replay/garantia de entrega única. A API aplica titularidade e regras de OS; Active/ClockSkew zero e decisão explícita foram implementados na AE.7 e conferidos em hom na AE.10.

A rota antiga/desconhecida retorna 404 no handler; método incorreto retorna 405/Allow: POST. Gateway com OpenAPI auth 2.0.0 e as duas rotas foi implantado coordenadamente e conferido na AE.10, sem Authorizer ou endpoint público de envio. Em nova implantação, preservar essa coordenação e a versão numérica auth consumida pelo Gateway.

## Consumo do Auth.Contracts

Infrastructure/ContractTests usam versão exata [1.0.0], sem checkout da API. NuGet.Config reserva o ID ao feed artifacts/auth-contracts. Na raiz, com AWS CLI autenticada e leitura autorizada do pacote:

```powershell
New-Item -ItemType Directory -Force artifacts/auth-contracts
aws s3api get-object --bucket mecanica-artifacts-121754142617-us-east-1 --expected-bucket-owner 121754142617 --key packages/GerenciamentoMecanica.Auth.Contracts/1.0.0/GerenciamentoMecanica.Auth.Contracts.1.0.0.nupkg artifacts/auth-contracts/GerenciamentoMecanica.Auth.Contracts.1.0.0.nupkg
aws s3api get-object --bucket mecanica-artifacts-121754142617-us-east-1 --expected-bucket-owner 121754142617 --key packages/GerenciamentoMecanica.Auth.Contracts/1.0.0/GerenciamentoMecanica.Auth.Contracts.1.0.0.nupkg.sha256 artifacts/auth-contracts/GerenciamentoMecanica.Auth.Contracts.1.0.0.nupkg.sha256
Get-FileHash artifacts/auth-contracts/GerenciamentoMecanica.Auth.Contracts.1.0.0.nupkg -Algorithm SHA256
Get-Content artifacts/auth-contracts/GerenciamentoMecanica.Auth.Contracts.1.0.0.nupkg.sha256
$env:NUGET_PACKAGES = Join-Path (Get-Location) artifacts/nuget/b9080cea9d303722ac072e89421505d728126cdb52d57549da4328b1e18ab4e9
```

Conferir os dois hashes contra b9080cea9d303722ac072e89421505d728126cdb52d57549da4328b1e18ab4e9 **antes do restore**. CI faz a comparação automaticamente e separa cache pelo hash. Não há scripts consumidores Get-AuthContracts/Test-AuthContracts no fluxo atual. [Produção/versionamento](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/PACOTE_AUTH_CONTRACTS.md).

## Build, testes e publish locais

SDK 10.0.402 fixado em [global.json](global.json), sem roll-forward. Docker Linux deve estar ativo para PostgreSQL/Testcontainers; não iniciar automaticamente nem ignorar testes indisponíveis. Os testes usam bootstrap/AWS simulados e PostgreSQL descartável, sem RDS/AWS reais.

```powershell
dotnet restore GerenciamentoMecanicaServerless.slnx
dotnet build GerenciamentoMecanicaServerless.slnx -c Release --no-restore
dotnet test GerenciamentoMecanicaServerless.slnx -c Release --no-build --no-restore
dotnet restore AuthFunction/AuthFunction.csproj -r linux-x64
dotnet publish AuthFunction/AuthFunction.csproj -c Release -r linux-x64 --self-contained false --no-restore -o artifacts/publish/function-linux-x64
sam validate --lint --template-file templates/auth.yaml --region us-east-1
sam build --template-file templates/auth.yaml
```

Publish local não implanta recursos. Conferir DLL/deps/runtimeconfig, Certificates/rds-ca.pem e Observability/observability.json/collector.yaml. Testes do handler são a execução integrada local, sem criar servidor/executável próprio. [Casos de uso da autenticação](Application/Authentication). Testes automatizados de transporte/Collector/telemetria da Lambda foram removidos por decisão; testes funcionais e MeterListener de integrações permanecem.

## Inicialização e persistência

Runtime__Environment=hom|prd e AWS_REGION=us-east-1 são obrigatórios ao inicializar autenticação real. Ausente/vazio/inválido falha, sem appsettings de banco/JWT ou alternativa local. O SDK usa a cadeia padrão de credenciais. Construir o handler/bootstrap não lê a configuração de autenticação; a primeira chamada de corpo válido pede o runtime. Observabilidade pode ser criada antes dessa leitura.

SSM String/WithDecryption=false fornece nove campos de banco/JWT e um segundo lote com alias/contrato email/v1. Banco/JWT: jwt-secret-arn/issuer/audience em base/v2 e endpoint/port/database-name/auth-secret-arn/auth-db-user/ssl-mode em database/v2. AWSCURRENT fornece JWT e credencial mecanica_auth; não usa Secret administrativo/database-api. Os valores ficam em memória e a inicialização bem-sucedida é reutilizada por handler. Falha não é armazenada como sucesso e permite nova tentativa; mudanças de Secret exigem nova inicialização.

Leitura AWS tem prazo de oito segundos e retries Standard existentes. Handler reserva um segundo do RemainingTime para resposta. Conexão/comando têm cinco segundos cada, sujeitos ao prazo total. NpgsqlDataSource é fábrica reutilizável, Pooling=false/TLS VerifyFull/GssEncryptionMode=Disable; cada lookup abre e descarta conexão inclusive em erro/cancelamento. CA pública está em [Infrastructure/Certificates/rds-ca.pem](Infrastructure/Certificates/rds-ca.pem).

SQL fixo parametrizado consulta id/name/document/status/email de customers e opera somente os desafios/limites do cliente elegível; não consulta orders. Consulta sem resultado/Inactive não é falha de banco. Dados corrompidos/status desconhecido/duplicatas são técnicos. Handler implementa descarte explícito para testes/local; a AWS não garante callback de descarte ao encerrar o ambiente de execução.

## Observabilidade

[Manifesto](AuthFunction/Observability/observability.json) configura regras/allowlists/filas e mensagens. SAM fixa OTel, X-Ray e New Relic sempre ativos; sem toggles de workflow. Uma linha JSON técnica por conclusão, com status/categoria/duração/IDs, mais TraceId/SpanId quando habilitado. Sem documento, token, payload, mensagem original ou stack trace.

Somente W3C traceparent seleciona pai OTLP; runtime/cabeçalho X-Ray não são pais da aplicação. Invocação → PostgreSQL e logs correlacionados; AlwaysOn coleta mesmo com pai não amostrado. Baggage/tracestate descartados. X-Ray/polling segue diagnóstico independente, sem árvore unificada prometida.

Layer Collector 0.22.0 (Collector 0.151.0) recebe OTLP em loopback e usa decouple, sem batch periódico adicional. Flush da aplicação compartilha até 500 ms entre sinais e preserva ao menos 500 ms para resposta. Filas limitadas/freeze/encerramento podem perder dados; sem entrega única/replay. Extensão lê License Key do Secrets Manager pela mesma role da função, sem chave em ZIP/variáveis/pipeline.

Mecanica.Auth mede solicitações de código e confirmações/duração por resultado (incluindo code-requested/rate-limited); seu total não é número de logins, que requer outcome=authenticated; Mecanica.Integrations mede acessos PostgreSQL/SSM/Secrets, separando operação/finalidade/resultado. Uma chamada lógica AWS é contada depois dos retries internos, não por tentativa de transporte.

## Workflows, artefatos e operação AWS

| Auth | Email | Fluxo compartilhado |
|---|---|---|
| [auth-ci](.github/workflows/auth-ci.yml) | [email-ci](.github/workflows/email-ci.yml) | PR/manual: solução completa, ambos SAMs, actionlint |
| [auth-publish](.github/workflows/auth-publish.yml) | [email-publish](.github/workflows/email-publish.yml) | Manual: testes/build SAM → ZIP/template/checksum imutável; OpenAPI somente auth |
| [auth-provision](.github/workflows/auth-provision.yml) | [email-provision](.github/workflows/email-provision.yml) | Manual: environment/artifact_commit, rede/dependências/VersionId → deploy SAM |
| [auth-destroy](.github/workflows/auth-destroy.yml) | [email-destroy](.github/workflows/email-destroy.yml) | Manual: consumidores ausentes → sam delete → ENIs/egress ausentes |

Wrappers fixam componente; quatro reusable-*.yml concentram etapas comuns com allowlists. Develop/hom e main/prd, OIDC e filas por componente/ambiente; sem aprovação adicional. Required checks configuration/dotnet preservados como gates finais do auth-ci, abrangendo também EmailFunction e seu SAM. Role CI somente leitura do pacote; Environment AWS_ROLE_ARN para auth e **AWS_EMAIL_ROLE_ARN** para email. Ver [Workflows Serverless](.github/workflows).

Auth usa SG da base e role persistente do bootstrap. Stack administra função/versões/alias/logs, egress TCP 5432/443 e seis campos auth/v2. Gateway consome versão numérica, não alias live/$LATEST; cada nova versão precisa de atualização Gateway.

ZIP usa chave nativa sam/auth/ambiente/commit e bucket versionado. Template imutável fixa **Bucket/Key/Version** antes do checksum. Republicação idêntica conserva VersionId original após comparar ZIP/template; divergência ou template antigo sem Version exige commit novo. Provision confere a versão exata e SAM usa Code.S3ObjectVersion. A chave é gravável, mas nova versão corrente não altera a seleção publicada.

No SAM CLI 1.167.0, CodeUri objeto preserva ZIP no sam delete; stack/templates operacionais são removidos. Contratos, artefatos de outras revisões, bucket, IAM e SG da base persistem. Auth-destroy exige Gateway/VPC Link ausentes e, ao final, ausência de ENIs/egress; pendência bloqueia sem exclusão manual/espera automática. Ordem: Gateway → API/auth → banco/email → base/SMTP4dev. Email-destroy checa API/auth antes de remover somente a stack email.

[Contratos SSM](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/arquitetura/CONTRATOS_ENTRE_REPOSITORIOS.md) · [GitHub Actions](https://github.com/pknfelps/GerenciamentoMecanicaServerless/actions).

O endpoint de negócio, quando provisionado, vem de /mecanica/ambiente/gateway/v2/endpoint, não de Function URL própria. Swagger/Postman pertencem à API e o contrato estático está neste repositório. Exemplos não garantem disponibilidade contínua; consultar o catálogo/execução do ambiente. PR/publish/provision futuros ficam com o mantenedor.

## EmailFunction local

A execução integrada local usa o Compose da API e o emulador Invoke do SAM. Inicializar db/smtp e o banco vazio conforme os READMEs da API e do banco. A rede padrão do Compose é gerenciamento-mecanica_default.

Na raiz Serverless, construir e iniciar a função em terminal dedicado com credenciais AWS fictícias. O template exige IDs fictícios somente para interpolação local; o emulador não provisiona recursos:

~~~powershell
sam build --template-file templates/email.yaml --build-dir artifacts/sam-email
$env:AWS_ACCESS_KEY_ID = 'local'
$env:AWS_SECRET_ACCESS_KEY = 'local'
$env:AWS_REGION = 'us-east-1'
$env:AWS_EC2_METADATA_DISABLED = 'true'
Remove-Item Env:AWS_SESSION_TOKEN, Env:AWS_PROFILE -ErrorAction SilentlyContinue
$parameters = @(
  'Environment=hom', 'ArtifactCommit=0000000000000000000000000000000000000000',
  'WorkloadSubnetIds=subnet-00000000000000001,subnet-00000000000000002',
  'EmailSecurityGroupId=sg-00000000000000001', 'NlbSecurityGroupId=sg-00000000000000002',
  'SmtpHost=smtp', 'SmtpPort=25', 'ZipKey=sam/email/hom/local.zip'
)
sam local start-lambda --template-file artifacts/sam-email/template.yaml --host 0.0.0.0 --port 3001 --docker-network gerenciamento-mecanica_default --parameter-overrides $parameters
~~~

O listener acessível ao container é exclusivo do ensaio local; limitar a porta 3001 ao computador/rede de desenvolvimento. A API no Docker Desktop usa Email__InvocationEndpoint=http://host.docker.internal:3001 e Email__FunctionName=EmailFunction. Encerrar o SAM com Ctrl+C. Credenciais fictícias não devem ser persistidas no perfil AWS; na inicialização real auth continua exigindo configuração AWS, sem alternativa de banco/JWT local. Os testes do handler usam bootstrap injetado.
