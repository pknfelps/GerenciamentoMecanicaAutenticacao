# LA.1 — Entrada Lambda Authorizer para orçamento

> **REGISTRO HISTÓRICO — desenho substituído em 22/10/2026.** A decisão vigente é autenticação por código solicitado pelo cliente, captura SMTP4dev e JWT Customer sem OS, com duas funções/stacks e repositório futuro GerenciamentoMecanicaServerless. [Plano AE.1–AE.10](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTENTICACAO_E_EMAIL.md). Os resultados abaixo referem-se à implementação LA local; não continuar sua ativação nem considerar este contrato o alvo da próxima entrega. Código e recursos não foram alterados por esta anotação.

**22/10/2026 — versão inicial e adaptação token de orçamento + documento implementadas e validadas localmente.** Este refinamento pertence ao [plano extra](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTHORIZER_ORCAMENTO.md), independente da Fase 3. Nenhuma operação AWS, publicação ou deploy foi executada.

## Implementação da versão inicial

- `LambdaEntryPoint.HandleAsync(JsonElement, ILambdaContext)` distingue proxy HTTP de eventos REQUEST. Eventos desconhecidos ou estruturalmente ambíguos falham de forma fechada. O handler público `FunctionHandler.HandleAsync` e suas respostas HTTP permanecem preservados.
- O entry point possui um FunctionRuntimeProvider compartilhado pelas duas entradas e um único conjunto de providers existente. Inicialização bem-sucedida é reutilizada; falha permite nova tentativa. Descarte do entry point descarta runtime/data source/observabilidade.
- `BudgetAuthorizerHandler` aceita somente PATCH da rota Orders/UUID/budget na conta 121754142617/us-east-1 e no stage hom/prd configurado. Confere ARN, path, método e requestContext; não recebe nem interpreta o corpo da aprovação.
- `X-Customer-Document` é comparado sem distinção de maiúsculas. Um valor é obrigatório; listas múltiplas, keys duplicadas e mirrors conflitantes são recusados. Um mirror idêntico em Headers/MultiValueHeaders é permitido. O documento é normalizado/validado antes do bootstrap, usando a implementação compartilhada existente.
- O caso de uso retorna também o UUID persistido do cliente no resultado interno. O contrato HTTP continua retornando apenas o JWT como string; não há alteração do pacote Auth.Contracts. Não é necessário interpretar o JWT para reconstruir o principalId.
- Cliente elegível recebe policy Allow limitada exatamente ao methodArn recebido, UUID como principalId e contexto escalar customerAuthorization contendo Bearer/JWT Customer. A Lambda não consulta a OS, não toma a decisão do orçamento e não chama a API.
- Negativas usam exceção com mensagem nativa fixa Unauthorized. Bootstrap/SQL/configuração/timeout usam AuthorizerFailureException com mensagem fixa, sem InnerException original. Falhas técnicas não viram negativas de elegibilidade nem retornam policy.
- Processamento do authorizer tem prazo conservador máximo de nove segundos desde a entrada, também limitado pelo RemainingTime menos um segundo. A configuração preserva seu teto de oito segundos e a consulta seus limites existentes, subordinados ao prazo total. Não se usa o timeout de 29 segundos da integração HTTP como orçamento do authorizer.

## Logging e limites desta etapa

Uma linha JSON técnica por conclusão do authorizer contém operação fixa, IDs de requisição, duração, categoria e tipo do erro técnico quando aplicável. Não contém documento, UUID do cliente, ARN da OS, JWT, evento, contexto, mensagem original ou stack trace.

Exceções Unauthorized/técnicas precisam atravessar o boundary Lambda para o Gateway tratá-las; o runtime gerenciado pode produzir seu próprio registro de erro/stack com a exceção fixa. Esses registros da plataforma não são produzidos pelo logger da aplicação e não foram inspecionados nesta validação local. Não houve supressão de logs ou mudança do nível de logging. Os códigos/formatos efetivos do Gateway e os registros nativos devem ser conferidos na integração posterior.

A classificação original de `/customers/validate` e seus contadores customizados não mudou. Logging OTLP, span dedicado do authorizer e flush desse caminho ainda pertencem à LA.5; não se declara observabilidade completa da nova entrada nesta etapa. O provider de integrações é compartilhado e as operações existentes continuam instrumentadas. AWS/Lambda Errors poderá incluir negativas esperadas emitidas por exceção quando a integração estiver ativa.

CPF/CNPJ e status Active representam a elegibilidade educacional existente, sem prova adicional de posse da identidade.

## Validação local da versão inicial

SDK 10.0.402 existente em diretório temporário, sem alteração de global.json. Docker já estava ativo; PostgreSQL 16.15 descartável/Testcontainers, sem RDS ou AWS reais.

- Restore e build Release aprovados, zero avisos/erros. Publish linux-x64/framework-dependent, SAM validate --lint e SAM build 1.167.0 aprovados. Certificado, configurações e runtimeconfig/dependências conferidos também no artefato extraído pelo SAM, sem assemblies de teste.
- Suíte completa: **320 testes aprovados, zero falhas e zero ignorados**: ApplicationTests 21, ContractTests 10, FunctionTests 119, InfrastructureTests 170.
- Cobertos header ausente/inválido/ambíguo/mirror, escopo da policy, hom/prd, falhas técnicas, cancelamento, ausência de tempo, clientes concorrentes e serialização com o DefaultLambdaJsonSerializer real.
- PostgreSQL real comprovou HTTP + authorizer usando uma única inicialização, principal/JWT do cliente correto, cliente ausente/Inactive, nova tentativa após falha, SQL técnico, fechamento de conexões e descarte do data source.
- Testes HTTP anteriores preservam 200/400/401/500 e a assinatura do FunctionHandler. O teste novo usa o entry point com ambos os formatos; isso não simula o runtime gerenciado ou o mapeamento do Gateway.
- A primeira execução encontrou uma corrida na verificação de cancelamento do teste novo: o WaitAsync pode terminar antes do finally da tarefa cancelada. O teste passou a conferir o token entregue à operação; a regressão PostgreSQL confere o fechamento efetivo das conexões. Não foi ocultada falha funcional ou ignorado teste.

Relatórios TRX locais em artifacts/la1-authorizer/final-tests; não são publicados pelo Git.

## Publicação ainda pendente

O SAM continua apontando para FunctionHandler. O novo entry point está incluído no assembly, mas **não está selecionado no template nem implantado**. Troca de Handler e preservação do empacotamento/VersionId pertencem à LA.4. O Gateway ainda não possui authorizer ou mapeamento do JWT interno (LA.2), a API continua respondendo 204 (LA.3) e a observabilidade da entrada nova será concluída na LA.5.

A adaptação LA.1 abaixo já consolidou o contrato de validação. Próximas etapas: integração Gateway (LA.2) e emissão/verificação de escopo na API (LA.3), antes de ativar o fluxo completo. Não conceder acesso à API usando apenas header/UUID; continuar com JWT interno assinado, autorização Customer e conferência transacional de titularidade. Não ativar esta função pelo console ou publicar apenas a troca de handler como se o fluxo completo estivesse concluído.

## Decisão posterior — token de orçamento + documento (22/10/2026)

O alvo aprovado no plano extra passou a exigir **credencial de orçamento e CPF/CNPJ**, sem login Customer separado na aprovação. Esta revisão foi implementada depois da versão inicial; os 320 testes acima continuam sendo sua evidência histórica. A nova regressão completa possui 369 testes, descritos abaixo.

- A API disponibilizará o token na resposta protegida da conclusão do diagnóstico, para captura pelo Postman nos testes internos. Sem páginas ou envio de e-mail para distribuir a credencial.
- O token assinado conterá **UUID do cliente e UUID da OS**, além de finalidade e expiração. Não conterá CPF/CNPJ.
- A Lambda verificará assinatura/emissor/audiência/finalidade/validade, comparará o UUID da OS com a rota e consultará **customers pelo UUID do cliente assinado**. O documento informado deve coincidir com o cadastro e o cliente deve estar Active.
- Token do cliente A com documento do cliente B será recusado, mesmo que ambos estejam ativos. Token válido não dispensa documento.
- A Lambda **não consultará orders**, não resolverá cliente por vínculo da OS e não precisará de grants adicionais sobre ordens. Comparar ID assinado com ID da rota não equivale a verificar a OS no banco.
- A API continuará responsável pela verificação autoritativa da titularidade, escopo da credencial interna e estado da OS dentro da transação. Decisão repetida será rejeitada pelo estado, sem tabela de tokens/revogação individual.
- Revisar `/customers/validate` para exigir credencial de orçamento junto ao documento e impedir emissão de JWT por CPF/CNPJ isolado. A aprovação não exigirá chamar essa rota separadamente.
- Preservar runtime compartilhado, policy exata e limites/sanitização; adaptar validação, JWT interno e testes antes de ativar o authorizer.

Esta decisão substitui o alvo inicial, sem reclassificar código existente como concluído para o novo contrato e sem modificar os checklists oficiais E5/E6/E7. Banco, IAM, SAM, Gateway e aplicações não foram alterados neste registro documental.

## Adaptação LA.1 — evidência local em 22/10/2026

O caso de uso agora recebe documento, credencial e OS esperada quando chamada pelo authorizer; valida credencial HS256 pelo adapter Infrastructure antes de abrir PostgreSQL. Consulta exclusivamente customers por UUID assinado. O contrato de headers/claims/expiração está em [CREDENCIAL_ORCAMENTO.md](CREDENCIAL_ORCAMENTO.md). Interface de emissão e JWT interno passaram a preservar a OS/finalidade e limitar a expiração à credencial pública.

POST /customers/validate mantém documento obrigatório no body e passa a exigir Authorization Bearer de orçamento. Ausência/ambiguidade de Authorization recusa sem bootstrap; formato do corpo inválido continua 400. O handler e entry point compartilham o runtime atual, com nova tentativa após falha e descarte. Credenciais não entram nos logs.

- Restore/build Release: zero avisos/erros. Publish linux-x64/framework-dependent e SAM validate --lint/build aprovados para a adaptação. Artefatos conferidos com certificado RDS e configurações; sem assemblies de teste no publish ou no pacote extraído pelo SAM.
- **369 testes aprovados, zero falhas e zero ignorados:** Application 22, Contract 10, Function 132, Infrastructure 205. PostgreSQL 16.15/Testcontainers real, Docker já ativo, sem AWS/RDS reais.
- Cobertos token + documento, assinatura/emissor/audiência/tipo/finalidade, expiração/nbf, claims ausentes/ambíguos, UUIDs, token de outra OS, token de A com documento de B, cliente inexistente/Inactive, falha SQL e timeout. Contexto inválido é rejeitado antes da abertura SQL quando aplicável.
- Comprovados runtime compartilhado/retry, chamadas concorrentes, fechamento de conexões, policy exata e JWT interno com escopo/expiração limitados. Role SQL sem acesso a orders consegue autenticar com UUID do token; não há ampliação de grants.
- Dois testes antigos de bootstrap inicialmente falharam porque seus requests ainda omitiam a nova credencial; foram atualizados para exercitar inicialização e prazo com Bearer presente. Nenhum teste foi ignorado e as expectativas de timeout/reuso foram mantidas.

Os relatórios locais estão em artifacts/la1-adaptation/final-tests. A versão inicial e esta adaptação não foram implantadas. Emissão pela API e enforcement do escopo da OS, contrato completo de aprovação, observabilidade do authorizer e ativação Gateway/SAM ainda dependem das etapas LA seguintes. Não publicar esta mudança isoladamente nem apresentá-la como integração AWS comprovada.
