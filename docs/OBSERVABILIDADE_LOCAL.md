# Observabilidade local da autenticação

**2026-10-15 — Decisão vigente sobre testes de Collectors/New Relic:** removidos os testes de transporte/conexão dos Collectors locais e New Relic na API/Lambda, os receptores OTLP fictícios e seus auxiliares. CI não executa mais as imagens Contrib para validar exporter/YAML. Não há cenários ignorados ou substituídos por mocks. Na Lambda, todos os testes de telemetria, incluindo instrumentação InMemory, foram removidos por decisão do mantenedor; testes funcionais HTTP/JWT/PostgreSQL e logs do contrato permanecem; configurações de produção, Compose manual, SAM/publish e preflights operacionais de credencial permanecem. Conectividade/ingestão e comportamento dos Collectors passam a conferência operacional após deploy. Resultados anteriores são históricos; E5 continua parcial. Nenhum deploy, workflow remoto ou alteração AWS nesta remoção.

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

O Compose inicia **somente o Collector**, com OTLP HTTP/protobuf em `127.0.0.1:4319`, memory_limiter/batch/debug, sem credencial ou persistência. Não executa a Function e permanece disponível para operação manual. Não há testes automatizados de telemetria na Lambda; a conferência dos sinais e dos Collectors é operacional.

Por decisão do mantenedor, os testes de transporte para Collector local e YAML de produção/New Relic foram removidos, incluindo destinos HTTP fictícios, cenários 401/429/503/lentidão/offline e o encaminhamento de portas de teste.

FunctionTests preserva os testes funcionais do handler, contrato HTTP e logs sanitizados. InfrastructureTests preserva configuração AWS simulada, documentos, JWT e integração PostgreSQL funcional. Foram removidos todos os testes de instrumentação, configuração de telemetria, correlação, sanitização OTLP, métricas e flush, inclusive os cenários misturados à fixture PostgreSQL e seus auxiliares. Os projetos não referenciam mais OpenTelemetry.Exporter.InMemory. Indisponibilidade do Docker falha os testes PostgreSQL, sem ignorá-los.

## Collector de produção no SAM

OpenTelemetry e a layer oficial amd64 0.22.0 são permanentes na Lambda implantada. O único Function/Observability/collector.yaml acompanha o ZIP e exporta para New Relic por Secrets Manager nativo. SAM fixa Telemetry__Enabled=true, endpoint 127.0.0.1:4318, URI /var/task/Observability/collector.yaml, GOMEMLIMIT=96MiB e nome do Secret do próprio ambiente. Não há parâmetros de desativação; o tracing nativo X-Ray também é fixo no SAM com Tracing: Active.

Configuração local JSON/Compose permanece opt-in e usa debug sem AWS/New Relic; isso não é uma opção de deploy. O YAML de produção preserva memory_limiter → decouple. Não há testes automatizados dos Collectors nem validação da imagem no CI; transporte, ciclo/fila/congelamento e ingestão exigem conferência operacional posterior.

Workflows continuam executando os testes .NET funcionais e conferindo os arquivos no publish. SAM/workflows mantêm suas validações estruturais; o preflight de metadados da layer/credencial continua obrigatório no provisionamento. [Operação, cadastro e sequência](https://github.com/pknfelps/GerenciamentoMecanicaInfraestrutura/blob/develop/docs/NEW_RELIC.md).

## Resultados históricos anteriores à adoção permanente

Os registros abaixo descrevem configurações antigas; não são instruções de ativação atuais.

## Resultado local — 14/10/2026

277 testes aprovados, zero falhas e zero ignorados (21 Application, 10 Contract, 105 Function, 141 Infrastructure). Restore/build Release, PostgreSQL/Collector reais, publish linux-x64, sam validate --lint, sam build e renderização do Compose aprovados. Certificado, observability.json, dependências e runtimeconfig.json conferidos no publish e no diretório de build do SAM; exporter InMemory ausente. Evidências finais em artifacts/e5-lambda/validation.json, IMPLEMENTACAO.md e accepted/*.trx. Nenhuma operação AWS nesta validação.

## Resultado local — 15/10/2026: Collector preparado no SAM

278 testes aprovados, zero falhas/ignorados: Application 21, Contract 10, Function 105 e Infrastructure 142. Restore/build Release sem avisos/erros; sam validate/cfn-lint e build com TelemetryEnabled=false/true aprovados. Publish linux-x64 e layout ZIP conferidos, com certificado/JSON/YAML/runtimeconfig/deps e sem exporter de teste. YAML validado com Contrib 0.151.0; basic recebeu três sinais sem reproduzir o payload, com GOMEMLIMIT=96MiB. A fixture executou os dois perfis e comprovou primeira invocação, identidade/correlação/sanitização e continuidade sem Collector. Resultados aceitos em artifacts/e5-lambda-collector/accepted/*.trx; resumo em validation.json e IMPLEMENTACAO.md. Nenhuma operação AWS; as comprovações reais da extensão e a ativação continuam pendentes.

## Workflows vigentes e validação posterior

Não há telemetry_enabled ou new_relic_enabled no auth-provision, nem parâmetros SAM equivalentes. Cada deploy sempre instala a layer e configura New Relic; o usuário escolhe apenas ambiente e commit publicado; X-Ray também é permanente, fixado como Active no SAM. A configuração local de instrumentação permanece disponível para operação manual; seus testes automatizados foram removidos, sem mudar a decisão de produção.

CI executa os testes .NET e valida SAM/workflows, sem teste de Collector/exporter; publish inclui configuração e certificado, provision exige revisão nova com telemetria fixa e auth-destroy mantém sam delete. Mudança da chave exige nova inicialização/versão da Lambda e atualização do Gateway. Runtime, HTTP/JWT, flush e sanitização não mudam. [Procedimento completo](https://github.com/pknfelps/GerenciamentoMecanicaInfraestrutura/blob/develop/docs/NEW_RELIC.md).

Não há testes de conexão com Collector local ou New Relic. Primeira chamada isolada, falhas de exportação, leitura IAM/Secrets real, decouple e ingestão precisam de conferência operacional na AWS. Nenhuma execução remota decorre desta simplificação.

**Validação após remoção dos testes de Collectors/New Relic — 15/10/2026:** build Release e 275 testes restantes aprovados, zero falhas/ignorados. Actionlint/ShellCheck aprovados nos dois CIs alterados. Evidências locais em artifacts/collector-tests-removal. Nenhum teste novo de conexão, Collector iniciado, workflow remoto ou operação AWS nesta validação; Docker utilizado somente pelos testes PostgreSQL preservados.

**Validação após remoção de todos os testes de telemetria da Lambda — 15/10/2026:** restore e build Release concluídos; 217 testes funcionais restantes aprovados (21 Application, 10 Contract, 49 Function e 137 Infrastructure), zero falhas e zero ignorados. Conferência dos quatro projetos de teste sem referências de instrumentação OpenTelemetry, exporters InMemory ou auxiliares de flush. Evidências locais em `artifacts/lambda-telemetry-tests-removal`. Instrumentação de produção preservada; nenhuma operação AWS ou execução remota.
