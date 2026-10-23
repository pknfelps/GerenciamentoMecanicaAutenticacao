# AE.3 — Preparação das operações transacionais

**22/10/2026 — componentes C# implementados e conferidos localmente.** [Contrato extra vigente](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/CONTRATOS_EXTRA_AUTENTICACAO_E_EMAIL.md). Esta preparação não troca o handler/rotas atuais, não envia e-mail e não publica recursos AWS. A composição HTTP e o Invoke da EmailFunction continuam na AE.6.

## Componentes

- Domain/Customers/CustomerAccessChallenge: valida estados, dados persistidos, cinco minutos/cinco tentativas e mudanças permitidas em C#.
- Domain/Customers/CustomerAccessRequestLimit: aplica 60 segundos, cinco solicitações por hora, renovação da janela e Retry-After arredondado para cima. Cooldown continua valendo se a janela de uma hora terminar antes dele.
- Application/Authentication/ICustomerAccessStore: contrato de criação, ativação, invalidação e confirmação.
- Infrastructure/Persistence/PostgreSqlCustomerAccessStore: consultas parametrizadas, transações Npgsql e medições técnicas de acesso pelo componente existente.
- Infrastructure/Authentication/CustomerAccessCode: código aleatório de oito dígitos ASCII, preservando zeros iniciais; PBKDF2-HMAC-SHA256 com salt de 16 bytes, 600.000 iterações, verificador de 32 bytes e comparação constante. Usa somente APIs nativas.

O mapeamento de CustomerRow foi compartilhado com o lookup anterior; ele continua consultando somente as quatro colunas anteriores. O novo store consulta também email, cujo [grant restrito foi posteriormente preparado e validado localmente](https://github.com/pknfelps/GerenciamentoMecanicaBancoDados/blob/develop/docs/AE3_PERMISSOES_AUTENTICACAO_2026-10-22.md). Não houve mudança de pacote, SQL de inicialização, grants ou código do handler nesta preparação.

## Operações

| Método | Comportamento |
|---|---|
| CreatePendingAsync | Consulta cliente primeiro; desconhecido encerra com log fixo, sem transação/tabela de autenticação/código/limpeza. Inactive ou e-mail inutilizável também encerra sem gravação. Para elegível, bloqueia por UUID, relê o cadastro, calcula o limite e persiste Pending/substituição na mesma transação. |
| ActivateAsync | Após captura confirmada pelo futuro chamador, transforma somente Pending válido em Ready; substituído/encerrado não é reativado. Expirado ou cliente removido/inativo é invalidado quando aplicável. |
| InvalidateAsync | Encerra somente Pending/Ready; Consumed e Invalidated não são reabertos ou alterados. Será usado pelo chamador após falha de envio, sem substituir a falha original se a tentativa de invalidação também falhar. |
| VerifyAndConsumeAsync | Relê desafio e cadastro, valida documento/código/Active/estado/prazo e confirma o resultado em transação. Negativa persiste a tentativa; sucesso prepara token, consome e só devolve após commit. |

CreatePendingAsync recebe documento já normalizado. VerifyAndConsumeAsync recebe documento já normalizado e código previamente validado pelo parser; também rejeita localmente ID vazio e código fora dos oito dígitos ASCII. A normalização/HTTP 400 continuam sendo responsabilidade dos casos de uso/parsers da AE.6.

O resultado de criação é interno e contém Customer/Email/Code apenas em memória para a futura montagem do envio. **Não serializar ou registrar esse resultado como resposta HTTP/log.** O handler montará somente challengeId/mensagem fixa para o 202 público. CustomerCodeRequestResult é classe comum: não gera ToString com seus dados como um record faria.

Cliente inexistente gera somente mensagem Information fixa Solicitação de autenticação para cliente não encontrado. Sem CPF, e-mail ou código nas propriedades. A limpeza oportunista é executada somente depois do commit de uma solicitação admitida; nunca roda no caminho de cliente inexistente. [Implementação e validação](AE3_LIMPEZA_EXPIRADOS.md).

## Transação e concorrência

Todas as operações de um cliente usam pg_advisory_xact_lock(hashtextextended(UUID em texto, 0)), parametrizado, antes dos locks das linhas de limite/desafios. A chave é calculada no PostgreSQL, não por GetHashCode da instância C#. Uma colisão apenas serializa clientes distintos. Não há SELECT FOR UPDATE nem escrita em customers.

As linhas de desafio são bloqueadas em ordem de UUID. O relógio é clock_timestamp do banco, lido depois dos locks. Datas persistidas são UTC; não são cronômetros em memória. Após derivação/assinatura, a confirmação relê o relógio para não consumir um código que expirou durante o processamento.

O store cria conexões/transações somente durante a operação e as descarta ao final. Nenhum lock fica aberto entre criação e futura chamada de envio. Ativação/invalidação são outras transações curtas. Usa timeout de comando de cinco segundos e o CancellationToken do chamador; não cria retry próprio. Em falha, tenta rollback com orçamento de um segundo, preservando a exceção original. Falha do rollback registra somente mensagem fixa/tipo, sem objeto Exception, stack trace, SQL ou mensagem interna.

Documento divergente e código incorreto de Ready válido incrementam failed_attempts e COMMIT antes de devolver NotEligible; a quinta invalida. Pending/encerrados não executam verificação PBKDF2 nem debitam tentativas. Cadastro ausente/Inactive invalida o desafio aberto. Hashes/dados persistidos incompatíveis geram falha técnica fixa, sem tentar reparar silenciosamente.

O assinador é recebido pela interface ICustomerTokenIssuer existente e chamado dentro da transação, antes do consumo/commit. Se lançar erro, o desafio continua utilizável conforme seu estado/prazo. Commit ambíguo ou perda de resposta podem consumir sem entregar token; não há replay, persistência do JWT ou promessa de entrega única.

## Testes para leitura e manutenção

Os testes usam nomes que descrevem a regra, sem exporters ou Collector:

| Exemplos de testes | O que comprovam |
|---|---|
| CooldownAllowsAnotherRequestOnlyAfterSixtySeconds | 59 segundos bloqueiam; 60 permitem, sem adiar o limite por uma negativa. |
| CooldownStillAppliesWhenTheOldHourHasEnded | A troca da janela não contorna os 60 segundos. |
| MissingCustomerReturnsBeforeCodeGenerationOrAnyAccessToTheAuthTables | Retira todas as permissões das tabelas novas no teste; desconhecido ainda retorna e só produz o log fixo. |
| WrongCodeOrDocumentCommitsEachAttemptAndTheFifthInvalidates | Lê o banco após cada negativa, conferindo que a tentativa não sofreu rollback. |
| ReplacingAChallengePreventsAnOldCaptureFromReactivatingIt | Captura antiga não reativa um desafio substituído. |
| ConcurrentRequestsAcrossStoresCreateOnlyOneChallenge | Instâncias distintas do store compartilham os locks do banco. |
| ConcurrentConfirmationsAcrossStoresReturnOnlyOneToken | Confirmações simultâneas não retornam dois tokens. |
| InsertFailureRollsBackTheNewLimitCountAndPreviousInvalidation | Revoga INSERT no PostgreSQL local para falhar depois das outras escritas; ambas são revertidas. |
| SigningFailureRollsBackAndLeavesTheCodeAvailableForRetry | Assinatura falha sem consumir; a mesma exceção original é preservada. |
| BrokenConnectionDoesNotReplaceTheOriginalFailureWithARollbackError | Encerra a conexão local durante a assinatura; rollback não mascara o erro e o código fica Ready. |
| CancellationWhileWaitingForTheCustomerLockDoesNotWrite | Aguarda o bloqueio aparecer em pg_stat_activity, cancela e confere ausência de gravação. |

As fixtures PostgreSQL 16.15 são descartáveis e não contêm CHECKs/FKs de negócio nas tabelas novas. O usuário local de testes recebe explicitamente os grants previstos, incluindo SELECT email; isso não substitui a conferência separada do ProvisionAuthUser.sql real, concluída localmente e referenciada acima. Cada teste também confere que não restaram conexões de autenticação abertas. Os testes ajustam somente datas persistidas locais para não esperar 60 segundos entre cenários.

Regressão da preparação transacional anterior: 309 testes aprovados, zero falhas/ignorados, sendo 40 novos (11 de regras e 29 PostgreSQL). EmailFunctionTests permanece estrutura sem casos; não conta como cobertura do envio. Restore/build Release sem avisos/erros. O SDK exigido 10.0.402 compilou; no Windows, os testes usaram VSTest 18.6/.NET 10.0.9 já instalados porque o runtime temporário 10.0.13 do SDK está incompleto (System.Net.Mail ausente). Nenhuma dependência foi acrescentada para contornar o ambiente. Evidências em artifacts/ae3-transactions, fora do Git.

## Próximos passos

Grants restritos e [limpeza oportunista](AE3_LIMPEZA_EXPIRADOS.md) preparados e validados localmente. AE.3 concluída no escopo local, com nova regressão completa de 328 testes aprovada. AE.6 conectará os componentes aos novos parsers/casos de uso/handler, montará respostas públicas, criará o assinador definitivo de dez minutos e invocará EmailFunction entre Pending/Ready. HTTP/JWT existentes foram preservados; não declarar esse fluxo novo implantado. Não houve alteração de AWS, SQL/grants, IAM, SAM, Collector ou workflows nesta preparação.

## Reconferência PostgreSQL local — 22/10/2026

[Registro consolidado](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/VALIDACAO_AE3_POSTGRESQL_LOCAL_2026-10-22.md): Job atual inicializa banco vazio, reconhece repetição e recusa hash divergente/banco não vazio sem marcador, sem alterar dados. Estrutura/seeds/instantes e grants reais conferidos em PostgreSQL 16.15 descartável. Nova regressão Serverless: 309 testes aprovados, zero falhas/ignorados; restore/build Release sem avisos/erros. Sem migração, regras de negócio no SQL, alteração de produção ou acesso AWS. Nessa reconferência anterior, limpeza ainda estava pendente. A implementação e os testes posteriores estão em [limpeza dos expirados](AE3_LIMPEZA_EXPIRADOS.md).
