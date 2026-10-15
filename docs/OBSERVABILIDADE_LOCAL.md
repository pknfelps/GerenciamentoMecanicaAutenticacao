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

## Limites

Sem alteração de SAM, layers, IAM, Gateway, workflows, SQL ou recursos AWS. Não habilitar exportação em hom/prd até preparar e comprovar o Collector local da Lambda, compatibilidade da layer, conectividade, tempo/memória e ingestão. X-Ray, correlação Gateway/API e New Relic continuam separados. E5.3/E5.13 permanecem parciais.


## Resultado local — 14/10/2026

277 testes aprovados, zero falhas e zero ignorados (21 Application, 10 Contract, 105 Function, 141 Infrastructure). Restore/build Release, PostgreSQL/Collector reais, publish linux-x64, sam validate --lint, sam build e renderização do Compose aprovados. Certificado, observability.json, dependências e runtimeconfig.json conferidos no publish e no diretório de build do SAM; exporter InMemory ausente. Evidências finais em artifacts/e5-lambda/validation.json, IMPLEMENTACAO.md e accepted/*.trx. Nenhuma operação AWS nesta validação.
