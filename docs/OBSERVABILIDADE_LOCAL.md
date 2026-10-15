# Observabilidade local da autenticação

A Function produz traces, métricas e logs OTLP somente com ativação explícita. A configuração versionada é [observability.json](../Function/Observability/observability.json); variáveis de ambiente prevalecem sobre o JSON. A leitura acontece uma vez, sem recarga.

## Configuração

| Campo | Valor inicial / regra |
|---|---|
| `Telemetry__Enabled` | `false` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Obrigatório quando habilitada; HTTP/HTTPS absoluto, sem usuário, senha, query ou fragmento |
| `Runtime__Environment` | `hom` ou `prd`, preservando a configuração AWS da autenticação |
| `AWS_REGION` | `us-east-1` |
| `OTEL_SERVICE_NAME` | `mecanica-auth` |
| `OTEL_RESOURCE_ATTRIBUTES` | Somente `service.name`, `service.version` e `deployment.environment.name` técnicos |
| `Metrics__EnabledMetrics__Mecanica.Auth` | `true`; não cria instrumentos quando desabilitado |

A versão padrão vem do commit nos metadados do assembly; sem commit, é `local`. Ambiente não define uma alternativa local de banco/JWT. A configuração real da autenticação continua exigindo SSM/Secrets Manager e rede para o RDS privado. Nos testes, o bootstrap é injetado e não acessa a AWS.

Listas de atributos, mensagens fixas, filtros de logs, dimensões, limites do histograma, filas, lotes e intervalos ficam no JSON. Esse arquivo é política de coleta: não acrescentar SQL, dados pessoais, credenciais, categorias internas ou IDs às dimensões. Headers OTLP e destinos/protocolos/timeouts específicos por sinal são rejeitados. Não há chave New Relic nesta entrega.

## Sinais

- Um span por invocação, incluindo parsing e inicialização tardia. Nome padrão `POST /customers/validate`, independente do caminho recebido. Consultas e abertura de conexão Npgsql ficam relacionadas à invocação.
- Contexto W3C, inclusive pai não amostrado. Todas as invocações são capturadas; baggage, tracestate e extração do contexto X-Ray permanecem desabilitados.
- `mecanica.auth.invocations`: uma contagem por invocação concluída.
- `mecanica.auth.invocation.duration`: segundos de processamento, incluindo inicialização quando ocorrer, antes do flush.
- Dimensões: `outcome` e `http.response.status_code`. Categorias: `invalid-request`, `invalid-document`, `not-eligible`, `authenticated`, `timeout`, `technical-failure`. 400/401 não são falhas técnicas.
- Uma linha JSON de conclusão no ILambdaLogger. Quando habilitada, recebe `traceId`/`spanId` e o mesmo acontecimento é enviado por um provider OTLP. Não há outro console, scopes ou coleta de stdout.

Antes da exportação, a Function remove SQL, parâmetros, conexão, conteúdo HTTP e dados de negócio. Exceções preservam apenas tipo e mensagem fixa; nenhuma mensagem original, exceção interna ou stack trace é exportada. Providers e listeners pertencem à instância do handler. Os construtores de teste aceitam instrumentação explícita; o handler padrão carrega a configuração versionada. Descarte explícito encerra providers/listener/meter e o runtime.

## Prazo e perdas

A autenticação mantém a reserva de um segundo do tempo restante da Lambda. Ao concluir, registra log/métricas, encerra o span e tenta exportar. O wrapper oficial recebe provider nulo somente para desabilitar seu ForceFlush implícito sem timeout; os providers reais continuam ativos.

O flush dispõe de até 500 ms **compartilhados**, recalculando o prazo antes de traces, métricas e logs. Preserva pelo menos 500 ms para resposta/serialização. O timeout OTLP inicial é 100 ms; se não houver esse tempo disponível, não inicia outra espera. Há filas de 2.048 registros, lotes de até 512, intervalo de um segundo e métricas periódicas de 30 segundos. A chamada isolada não depende desses temporizadores.

Falha ou demora do Collector não substitui a resposta calculada. Um diagnóstico técnico usa mensagem fixa, sem exceção completa. Não há fila persistente, retries próprios, reprocessamento ou garantia de entrega única. Encerramento abrupto, fila cheia, pouco tempo restante e indisponibilidade prolongada podem causar perdas. A validação local não comprova congelamento/descongelamento na AWS.

## Execução e validação

Requer o SDK fixado em `global.json`, pacote compartilhado disponível no feed local e Docker Desktop com engine Linux já ativo. O procedimento não inicia Docker Desktop.

```powershell
docker compose -f compose.observability.yaml config
docker compose -f compose.observability.yaml up -d
docker compose -f compose.observability.yaml logs otel-collector
docker compose -f compose.observability.yaml down

dotnet restore GerenciamentoMecanicaAutenticacao.slnx
dotnet build GerenciamentoMecanicaAutenticacao.slnx -c Release --no-restore
dotnet test GerenciamentoMecanicaAutenticacao.slnx -c Release --no-build
dotnet publish Function/Function.csproj -c Release -r linux-x64 --self-contained false -o artifacts/function
sam validate --lint
sam build --template-file template.yaml
```

O Compose inicia **somente o Collector**, com OTLP HTTP/protobuf em `127.0.0.1:4319`, memory_limiter/batch/debug, sem credencial ou persistência. Não executa a Function. A chamada integrada local acontece pelos testes do handler, sem script ou executável novo.

O teste `FirstIsolatedInvocationExportsToCollectorAndStoppedCollectorDoesNotBreakAuthentication` usa o mesmo YAML, inicia seu próprio Collector em porta aleatória com Testcontainers e PostgreSQL descartável. Uma única invocação deve entregar os três sinais; depois o Collector é parado e a autenticação continua. Assim, esse teste não depende do Compose manual estar ativo. Use `dotnet test InfrastructureTests/InfrastructureTests.csproj -c Release --filter Name~FirstIsolatedInvocation` para repetir essa conferência.

FunctionTests cobre configuração, opções nativas, recursos, filtros, dimensões, contexto, correlação, orçamento compartilhado, endpoint lento e descarte. InfrastructureTests amplia a fixture PostgreSQL existente para spans relacionados, erro real sanitizado, cancelamento, fechamento de conexão e inicialização reutilizada. Todos os testes anteriores permanecem; indisponibilidade do Docker falha os testes de integração, sem ignorá-los.

## Collector preparado no SAM

O Collector da API pertence ao EKS/infraestrutura; o da autenticação é uma extensão no próprio ambiente da Lambda. Seu YAML está em [Function/Observability/collector.yaml](../Function/Observability/collector.yaml), incluído no build/publish e no ZIP. Não há dependência de outro repositório para empacotá-lo.

O parâmetro SAM `TelemetryEnabled` aceita somente `true` ou `false` e tem padrão `false`. A condição `EnableTelemetry` controla a layer e suas variáveis. Desabilitado, não há layer, endpoint/URI/GOMEMLIMIT adicionais nem providers da aplicação. O template fornece `Telemetry__Enabled=false`; o JSON também permanece desabilitado. O auth-provision recebe telemetry_enabled=false por padrão e passa TelemetryEnabled explicitamente em toda execução, determinando a configuração da nova versão. Com true, consulta antes os metadados da layer do template e falha se não houver acesso; a permissão lambda:GetLayerVersion ainda precisa ser preparada no bootstrap antes da ativação. CI valida o YAML; auth-publish confere JSON/YAML no build. Templates sem TelemetryEnabled são rejeitados com orientação de republicar. A atualização do Gateway é necessária para consumir a nova versão numérica; versões antigas já publicadas não são modificadas por esse parâmetro.

| Configuração quando habilitada | Valor |
|---|---|
| Layer Collector-only | `arn:aws:lambda:us-east-1:184161586896:layer:opentelemetry-collector-amd64-0_22_0:1` |
| Release da layer / Collector | `0.22.0` / `0.151.0` |
| `Telemetry__Enabled` | `true` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://127.0.0.1:4318` |
| `OPENTELEMETRY_COLLECTOR_CONFIG_URI` | `/var/task/Observability/collector.yaml` |
| `GOMEMLIMIT` | `96MiB` |

A layer é somente o Collector, sem outra instrumentação .NET. ARN e arquitetura amd64 são fixos para us-east-1/x86_64. [Release oficial](https://github.com/open-telemetry/opentelemetry-lambda/releases/tag/layer-collector%2F0.22.0) e [configuração da extensão](https://github.com/open-telemetry/opentelemetry-lambda/blob/layer-collector/0.22.0/collector/README.md). A existência/acessibilidade desse ARN na AWS e as permissões para utilizá-lo ainda precisam ser conferidas antes de ativar.

O YAML recebe somente OTLP HTTP/protobuf em loopback, com memory_limiter de 64 MiB, margem de 16 MiB e verificação a cada segundo. O limite Go de 96 MiB é uma meta de memória do processo Collector, não uma reserva nem limite isolado da extensão; ela compartilha os 512 MB da função. Os três pipelines usam debug basic, sem batch, filas persistentes, credenciais, destino externo ou mudança de identidade. Não há receiver para stdout/logs da plataforma nem exporter X-Ray. A extensão pode usar eventos nativos de ciclo de vida internamente; eles não integram os pipelines de coleta definidos aqui.

O debug basic emite resumos de lotes e não exibe os registros completos. Sem batch adicional, os lotes recebidos do SDK seguem diretamente ao exporter diagnóstico. O flush existente continua limitado a 500 ms compartilhados, com reserva de resposta e timeout OTLP de 100 ms. A futura exportação para New Relic exigirá nova revisão do ciclo de vida/filas da extensão; a validação atual não comprova congelamento, entrega durável ou consumo real de recursos.

### Conferência local do YAML empacotado

```powershell
docker run --rm --volume "${PWD}/Function/Observability/collector.yaml:/etc/otelcol-contrib/collector.yaml:ro" otel/opentelemetry-collector-contrib:0.151.0 validate --config=/etc/otelcol-contrib/collector.yaml

dotnet test InfrastructureTests/InfrastructureTests.csproj -c Release --filter Name~FirstIsolatedInvocation
```

A fixture executa dois casos: configuração original do Compose com Contrib 0.161.0 e configuração copiada ao output com Contrib 0.151.0. No segundo, overrides nativos de `--config=yaml:...` alteram somente o bind para `0.0.0.0:4318` (acesso do teste ao container) e debug para detailed (inspeção). O arquivo empacotado permanece loopback/basic. O teste confere os três sinais da primeira chamada isolada, identidade/correlação, ausência de dados sensíveis e autenticação após parar o Collector. Bootstrap injetado e PostgreSQL descartável evitam acesso AWS/RDS reais.

Essa execução usa o container Contrib correspondente, não o binário da extensão ou o ambiente real da Lambda. A validação do SAM é local; não baixar layers pela API AWS nem executar `sam local invoke` que consulte a conta nesta entrega. Deploy/ativação, permissões, congelamento e memória/tempo reais continuam pendentes.

## Limites

SAM, YAML e workflows do Collector preparados, com padrão desabilitado. Sem alteração de IAM, Gateway, SQL ou recursos AWS; nenhum workflow remoto executado. Não habilitar exportação em hom/prd até conferir layer/permissões e comprovar extensão, conectividade, tempo/memória e ingestão. X-Ray, correlação Gateway/API e New Relic continuam separados. E5.3/E5.13 permanecem parciais.


## Resultado local — 14/10/2026

277 testes aprovados, zero falhas e zero ignorados (21 Application, 10 Contract, 105 Function, 141 Infrastructure). Restore/build Release, PostgreSQL/Collector reais, publish linux-x64, sam validate --lint, sam build e renderização do Compose aprovados. Certificado, observability.json, dependências e runtimeconfig.json conferidos no publish e no diretório de build do SAM; exporter InMemory ausente. Evidências finais em artifacts/e5-lambda/validation.json, IMPLEMENTACAO.md e accepted/*.trx. Nenhuma operação AWS nesta validação.

## Resultado local — 15/10/2026: Collector preparado no SAM

278 testes aprovados, zero falhas/ignorados: Application 21, Contract 10, Function 105 e Infrastructure 142. Restore/build Release sem avisos/erros; sam validate/cfn-lint e build com TelemetryEnabled=false/true aprovados. Publish linux-x64 e layout ZIP conferidos, com certificado/JSON/YAML/runtimeconfig/deps e sem exporter de teste. YAML validado com Contrib 0.151.0; basic recebeu três sinais sem reproduzir o payload, com GOMEMLIMIT=96MiB. A fixture executou os dois perfis e comprovou primeira invocação, identidade/correlação/sanitização e continuidade sem Collector. Resultados aceitos em artifacts/e5-lambda-collector/accepted/*.trx; resumo em validation.json e IMPLEMENTACAO.md. Nenhuma operação AWS; as comprovações reais da extensão e a ativação continuam pendentes.

## Integração dos workflows — 15/10/2026

O job configuration de auth-ci valida o YAML pelo comando nativo do Collector 0.151.0. Os testes .NET continuam obrigatórios, com os dois cenários de Collector e Docker ativo. auth-publish inclui checks dos dois arquivos de observabilidade no build SAM, sem alterar o pacote compartilhado, a publicação imutável ou os checksums.

auth-provision oferece telemetry_enabled=false por padrão, exige template novo com TelemetryEnabled e passa a opção explicitamente ao SAM. Somente habilitado consulta a layer referenciada pelo template; a projeção da resposta contém apenas ARN, arquiteturas e tamanho. O resumo não declara entrega ao destino. Não há novo job, aprovação ou chamada direta CloudFormation. auth-destroy permanece inalterado: sam delete exclui a função, não a layer pública.

Esta integração é local. Após PR/main, executar publish de nova revisão → provision com false. Antes de true, preparar IAM/conferir layer e planejar a validação AWS. Atualizar o Gateway para consumir a nova versão numérica, quando necessário. APIs/SSM v2/OpenAPI e regras JWT permanecem iguais. E5.3/E5.13 continuam parciais.
