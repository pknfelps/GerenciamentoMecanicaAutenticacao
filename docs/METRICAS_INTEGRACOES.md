# Métricas dos acessos às integrações

API e autenticação usam o meter `Mecanica.Integrations` e o contador `mecanica.integrations.operations`. Cada ponto representa **um acesso técnico concluído**, não uma requisição HTTP. Uma requisição pode abrir uma conexão, executar vários comandos e concluir uma transação. Um processo interrompido pode não registrar o acesso em andamento.

| Dimensão | Valores |
|---|---|
| integration | postgresql, ssm, secretsmanager |
| operation | connection.open, command.execute, transaction.begin, transaction.commit, transaction.rollback, get_parameters, get_secret_value |
| purpose | application, bootstrap, readiness, telemetry |
| outcome | success, failure, timeout, canceled |

O total soma todos os resultados. O percentual de falhas considera `(failure + timeout) / (success + failure + timeout)`; cancelamentos ficam separados e denominador ausente não vira zero. Uma exceção SQL conta como falha do comando, inclusive se posteriormente convertida em conflito HTTP. Isso não equivale necessariamente a HTTP 500 ou indisponibilidade do banco.

## Limites da medição

- A abertura mede a aquisição lógica da conexão, inclusive quando atendida pelo pool; não representa necessariamente uma nova conexão física.
- Cada chamada Dapper é uma operação; um lote com vários statements continua sendo uma chamada. Criação dos parâmetros/modelos e mapeamento do domínio ficam fora dessa medição. Consulta sem linhas é sucesso.
- Início, commit e rollback são operações separadas. Não medir o método inteiro do repositório nem descartar conexões como operações.
- SSM mede uma chamada lógica em lote a GetParameters. Secrets Manager mede cada GetSecretValue. O resultado incorpora as retentativas atuais do SDK, sem contar cada tentativa física.
- Resposta AWS obtida seguida de configuração inválida continua sendo chamada bem-sucedida; a falha de configuração mantém seu diagnóstico próprio.
- TimeoutException, timeout interno do driver ou cancelamento pelo prazo próprio são timeout. Quando o token do chamador já está cancelado, OperationCanceledException é canceled; isso inclui o orçamento global propagado pela Lambda. A métrica de invocação continua classificando o timeout do handler independentemente.
- readiness identifica a conexão da probe; telemetry identifica conexão/comando da leitura do histórico dos indicadores de OS. Os painéis principais consideram apenas application/bootstrap.

Não exportar SQL, parâmetros, conexões, ARNs, documentos, nomes de Secrets, mensagens de exceção ou stack traces. Somente as quatro dimensões permitidas; serviço, versão e ambiente reutilizam o Resource existente. As métricas independem da amostragem de traces. As métricas nativas de pools do Npgsql não foram habilitadas.

## Inicialização e descarte

O único provider de métricas da API é criado antes de carregar SSM/Secrets Manager e reutilizado durante a execução. Traces/logs mantêm sua composição habitual. As views e meters selecionados continuam no `observability.json`, incluindo os indicadores HTTP, runtime e de negócio existentes.

`Telemetry:StartupShutdownTimeoutMilliseconds=3000` limita o encerramento nativo do provider, inclusive após falha de inicialização. Shutdown coleta as medições finais; não acrescentar outro ForceFlush ou retentativas. A indisponibilidade do Collector pode impedir o envio. O diagnóstico é fixo, sem conteúdo das exceções. Sem telemetria e na geração oficial OpenAPI não há novos instrumentos/providers.

Na Lambda, a instância existente de observabilidade disponibiliza o componente ao bootstrap e ao lookup PostgreSQL. Ele usa o mesmo provider e flush compartilhado de 500 ms, sem flush por acesso e sem novo exporter. Runtime bem-sucedido é reutilizado; nova inicialização após falha produz novas operações reais.

## Conferência local e posterior

Os testes usam MeterListener para observar o contador nativo e PostgreSQL/Testcontainers nas fixtures existentes. Não usam exporter fictício, destino New Relic ou suíte de Collector. Docker deve estar aberto para executar a regressão completa; nenhum teste indisponível deve ser ignorado.

Depois dos PRs: api-deploy → auth-publish/auth-provision → Gateway consumindo a nova versão numérica → newrelic-provision para atualizar o dashboard. Não há alteração de IAM, Collector, SQL ou workflows. Conferir séries hom e filtros prd sem interpretar ausência como saúde. A credencial lida pela extensão/Collector não faz parte dos acessos medidos pela aplicação.

Operações e percentuais são agregados de pontos recebidos, sem replay, persistência ou entrega única. Falhas controladas e os cinco alertas existentes precisam de validação implantada e autorização própria. E5.8 permanece parcial até comprovar os novos sinais.

### Validação local de 19/10/2026

Build Release e publish da API/Lambda aprovados; Lambda conferida com SDK 10.0.402, RID linux-x64 e self-contained=false. Certificado RDS, manifesto com o novo meter, dependências/runtimeconfig e ZIP local conferidos, sem exporter de teste. SAM validate --lint/build aprovados; Kustomize hom/prd renderizado. Terraform fmt/init sem backend/validate e template renderizado como JSON válido; quatro painéis adicionados sem sobreposição e sem alterar os outros dashboards, cinco alertas ou lockfile.

Regressão completa concluída após o mantenedor abrir o Docker: **1.187 testes API e 245 testes Lambda aprovados, zero falhas e zero ignorados**, sem filtros. Inclui PostgreSQL/Testcontainers, abertura/consulta, consulta sem resultado, erro de domínio fora da medição SQL, commit/rollback, readiness, histórico de telemetria e lookup da autenticação. Os novos cenários passaram sem ajustes adicionais de código. A execução anterior sem Docker (1.021/205 testes) foi parcial e está substituída por esta conferência completa. Não houve inicialização automática do Docker.

OpenAPI idêntico: SHA-256 D784C3B554A65C4F8CFE732C0975B8BFEDAF78D67F9C6ED69E2B07220C5F5D84. Avisos anteriores de dependências/analisadores permanecem. Evidências locais em artifacts/e5-integrations na API e na autenticação. Nenhum workflow remoto, deploy, consulta AWS ou publicação New Relic foi executado. E5.8 permanece parcial.
