# AE.3 — Limpeza oportunista dos registros expirados

**22/10/2026 — implementada e validada localmente.** Integra a preparação da AE.3; não substitui a composição HTTP/envio da AE.6 nem representa implantação AWS.

## Regra vigente

Depois que CreatePendingAsync confirma o commit de uma solicitação admitida, o PostgreSqlCustomerAccessStore tenta limpar até **100 registros de cada tabela**, no conjunto global das tabelas, sem espera de 24 horas:

| Tabela | Elegibilidade |
|---|---|
| customer_auth_challenges | expires_at menor ou igual ao instante UTC atual do banco, independentemente do estado |
| customer_auth_request_limits | window_expires_at menor ou igual ao instante atual e last_requested_at menor ou igual a agora menos 60 segundos |

O código continua expirando em cinco minutos desde a criação. A exclusão não é necessária para rejeitar um código expirado. Consumed/Invalidated ainda dentro dessa validade são preservados até expires_at; não há recuperação ou reativação de desafio.

Um limite só é removido depois de terminar tanto a janela de uma hora quanto o cooldown. Se a janela terminou há 20 segundos, mas a última solicitação ocorreu há 30 segundos, o registro permanece para não permitir contornar o cooldown. Não se espera mais um dia após esses prazos.

Cliente inexistente, Inactive ou com e-mail inutilizável, pedido recusado por limite/cooldown e criação que sofreu rollback não acionam limpeza. Ativação, invalidação e confirmação também não a acionam. Os grants de DELETE já preparados são suficientes.

## Transação, concorrência e falhas

- O desafio/limite novo é confirmado primeiro. A limpeza usa a mesma conexão, em outra transação curta; falha na limpeza não desfaz ou troca o resultado já persistido.
- SQL fixo e parametrizado por instante, limite de lote e fim do cooldown. Ordena por expiração/identificador e usa FOR UPDATE SKIP LOCKED nas duas seleções. Linhas ocupadas por outra transação ficam para uma próxima oportunidade, inclusive limites em renovação.
- Não adquire advisory locks durante a limpeza nem bloqueia customers. As quatro operações de cliente mantêm seu advisory lock e ordem de bloqueio anteriores; apenas o helper de transação foi compartilhado com a limpeza.
- A transação de limpeza recebe um CancellationToken vinculado ao chamador, com prazo de um segundo para o conjunto das operações, não por tabela. Em falha, o rollback reutiliza o orçamento independente de um segundo já existente. Cancelamento PostgreSQL e descarte seguem as APIs nativas, sem garantia de resposta em exatamente um segundo.
- Se o chamador já cancelou depois do commit, a limpeza é dispensada e o resultado persistido é preservado. Falhas posteriores de limpeza são tratadas com mensagem Warning fixa e tipo da exceção, sem objeto Exception, mensagem original, stack trace, SQL, documentos ou códigos. O diagnóstico de eventual falha de rollback também permanece sanitizado.
- Ambas as exclusões pertencem à mesma transação: se a segunda falhar, a primeira também sofre rollback. Isso não afeta a transação anterior que admitiu o pedido.
- Reutiliza as medições existentes de acesso PostgreSQL; não cria novo instrumento, provider ou dimensão.

Não há job, agendamento, migration, trigger, nova constraint ou índice. O tamanho do lote limita a quantidade apagada por solicitação, não garante eliminar todo o estoque de expirados nem elimina o custo da busca. Sem solicitações admitidas, os registros podem permanecer indefinidamente; com backlog maior que o lote, a remoção é gradual.

## Testes e verificação

Dezenove novos casos na fixture PostgreSqlCustomerAccessStoreTests, com PostgreSQL 16.15 descartável e dados fictícios:

| Teste | O que confere |
|---|---|
| EachAdmittedRequestDeletesAtMostOneHundredExpiredRowsFromEachTable | 105 expirados viram cinco em cada tabela; nova solicitação admitida remove os cinco restantes e preserva os registros novos. Os dados já são elegíveis dentro das antigas 24 horas. |
| CleanupPreservesEveryChallengeWhoseFiveMinuteLifetimeHasNotEnded | Preserva Pending, Ready, Consumed e Invalidated ainda não expirados. |
| CleanupRequiresBothTheEndOfTheHourAndTheEndOfTheCooldown | Mantém janela ativa e cooldown ativo; remove somente quando ambos terminaram. |
| ARequestThatIsNotAdmittedDoesNotRunCleanup | Cliente ausente/inativo/e-mail inválido e rate limit não apagam linhas. |
| ARequestThatRollsBackDoesNotRunCleanup | Erro real de INSERT não aciona limpeza. |
| CleanupFailureRollsBackOnlyCleanupAndKeepsTheCommittedRequest | Revoga DELETE em cada tabela localmente; inclusive falha na segunda tabela reverte a primeira exclusão, preservando o pedido e log sanitizado. |
| CleanupSkipsLockedRowsAndPreservesALimitBeingRenewedByAnotherTransaction | Duas conexões reais: linhas bloqueadas são puladas, as livres são apagadas e a renovação concorrente do limite é preservada. |
| CleanupTimeoutKeepsTheCommittedRequestAndReturnsWithoutWaitingForATableLock | Lock real de tabela depois do commit faz a limpeza expirar; o pedido permanece e a transação de limpeza é revertida. |
| CancellationAfterTheRequestCommitSkipsCleanupAndPreservesTheResult | Cancelamento depois do commit dispensa limpeza e não perde o resultado. |
| OtherChallengeOperationsDoNotRunCleanup | Ativação/invalidação/confirmação não fazem exclusão oportunista. |

Somente nos dois testes de pós-commit, MeterListener nativo observa o sucesso de transaction.commit para adquirir um lock real ou cancelar o token no instante certo. Não há hook de produção, exporter fictício, dado sintético de telemetria ou nova suíte de Collector. O helper AfterFirstCommit existe exclusivamente nesses testes.

Restore/build Release aprovados, zero avisos/erros. Regressão completa: **328 testes aprovados, zero falhas/ignorados**, sendo 213 Infrastructure, 73 Function, 32 Application e dez Contract. EmailFunctionTests continua sem casos e não representa cobertura de envio. Cada caso da fixture confere ausência de conexões de autenticação restantes; containers são descartados por Testcontainers.

Compilação pelo SDK 10.0.402; testes pelo VSTest 18.6/runtime completo .NET 10.0.9 instalado, preservando o contorno local já documentado para o runtime temporário 10.0.13 incompleto. Nenhuma nova dependência. Publish linux-x64 sem self-contained aprovado, com assembly de Infrastructure, certificado RDS, JSON/YAML e runtimeconfig presentes, sem assemblies de testes/InMemory.

Evidências em artifacts/ae3-cleanup, fora do Git. Nenhuma alteração em esquema/seeds/grants, HTTP/JWT, handler, configuração de runtime, SAM, workflows, infraestrutura ou recursos AWS nesta etapa. A conferência do esquema/Job/grants reais permanece no [registro anterior](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/VALIDACAO_AE3_POSTGRESQL_LOCAL_2026-10-22.md).

**AE.3 concluída no escopo de preparação e validação local.** Próxima etapa AE.4: infraestrutura/IAM/SMTP4dev. Composição do novo fluxo HTTP/envio segue na AE.6 e implantação/comprovação na AE.10, mediante autorização própria.
