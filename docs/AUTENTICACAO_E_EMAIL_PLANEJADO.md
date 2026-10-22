# Autenticação e envio centralizado — organização planejada

**22/10/2026 — contratos consolidados e estrutura AE.2 implementada; fluxo de código/e-mail ainda pendente.** O [plano central AE.1–AE.10](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTENTICACAO_E_EMAIL.md) substitui o Authorizer e o token por OS. GitHub/raiz/remote já se chamam GerenciamentoMecanicaServerless; autenticação atual preservada.

## Organização e responsabilidades

- Nome atual: GerenciamentoMecanicaServerless. Function/FunctionTests já foram movidos localmente para AuthFunction/AuthFunctionTests na AE.2. Preservar Domain/Application/Infrastructure da autenticação, sem obrigar renomeação ampla de assemblies/namespaces ou de recursos AWS existentes.
- EmailFunction/EmailFunctionTests já têm estrutura local independente; composição/handler/transporte ainda serão implementados, com handler/ZIP/role/logs separados e sem referência ao domínio ou banco da autenticação. Uma solução e um repositório não significam execução ou privilégios compartilhados.
- templates/auth.yaml administra somente a stack auth atual; templates/email.yaml será criado para a nova stack email em AE.5/AE.8. Configurações de deploy separadas; alteração/destroy de uma não administra a outra.
- AuthFunction valida cliente, desafios e JWT e monta a mensagem do código. EmailFunction realiza o transporte SMTP de mensagens produzidas por API/auth. Não implementar SMTP nas duas origens, nem criar endpoint público de envio.

## Fluxo do cliente e captura

POST /customers/auth/request-code recebe document, cria desafio para cliente Active e chama envio síncrono por IAM; responde genericamente com challengeId, sem código. POST /customers/auth/verify-code recebe challengeId/code/document e valida cadastro/estado/expiração/tentativas/consumo para emitir JWT Customer de cliente, sem order_id e sem documento. AE.1 consolidou respostas/limites; implementar conforme o contrato central, ainda sem endpoints novos.

O cliente inicia o fluxo; o mecânico não emite acesso. O operador consulta o e-mail no SMTP4dev por acesso privado ao cluster para simular recebimento. A captura não comprova acesso à caixa pessoal de e-mail. SMTP4dev não fará relay externo e não terá volume persistente. Desafios ficam no PostgreSQL; caixa perdida exige novo código, sem endpoint que recupere seu valor.

Auth precisa de SELECT de email e grants restritos sobre as tabelas de desafios e limites por documento, sem acesso a orders/usuários internos ou admin. Email não recebe credencial SQL/JWT. Persistir verificador protegido, limitar solicitações/tentativas, invalidar reenvio anterior e consumir uma única vez atomicamente. Falhas de envio/bootstrap/SQL permanecem técnicas e não expõem conteúdo.

JWT válido é usado diretamente na API, sem chamada prévia de sessão ou envio automático em 401. A API confere Active/titularidade, preserva Mechanic e aplica estado/transação da decisão. A rota antiga customers/validate e o Authorizer devem ser removidos/adaptados sem manter bypass por CPF ou token de orçamento.

## Publicação e reutilização

Auth e email terão CI/publish/provision/destroy próprios. Compartilhar por workflow_call setup/build/package, checksum/VersionId imutável, OIDC/deploy e diagnósticos realmente equivalentes; manter templates/roles/prefixos/testes/bloqueios específicos e entradas validadas. Não aceitar shell arbitrário como parâmetro nem confundir separação de YAML com isolamento IAM.

Jobs reutilizáveis selecionam Environment; preservar política branch/ambiente e aprovações atuais. Fixar revisão do código/artefato, checar arquivos e versão exata de cada ZIP. CI deve cobrir mudanças compartilhadas e emitir os required checks, que serão revisados na migração.

Gateway continua usando versão numérica da auth. API/auth usam alias estável da função email por ambiente, com grants restritos e versão auditável; publicar correção de SMTP não exige republicar consumidores somente por troca do número. Catalogar esse consumo separadamente do auth/v2 existente.

Antes de renomear GitHub/local, preparar confiança OIDC de transição no bootstrap por plano autorizado, revisar links/remotes/uses, variáveis/Environments, escopo dos artefatos e checks. Atualizar CodeUri, solução e caminhos de testes/publish. Retirar confiança antiga após migração; não renomear automaticamente stacks/função auth nem expor segredos em arquivos.

## Dependências, testes e limites

Base/SMTP4dev precede email; banco/migração e email precedem auth/API; Gateway vem depois. No descarte, Gateway → API/auth → banco/email → base. Auth-destroy mantém seu bloqueio Gateway; email-destroy exige API/auth ausentes. Dois SAMs permitem retirar auth sem apagar o envio ainda consumido pela API.

Manter SDK/runtime, bootstrap reutilizado, TLS do RDS, deadlines, sanitização e observabilidade existentes. AE.1 definiu SAM local start-lambda/Invoke v1 e SMTP4dev do Compose, sem duplicar SMTP na API; auth mantém bootstrap injetado nos testes. A execução Docker será comprovada na implementação.

Testar desafios/claims/titularidade, reuso/concorrência/cancelamento, efeitos de falhas SQL/Invoke/SMTP, consumo único, ausência de dados sensíveis e mensagens de API/auth na captura. Docker deve estar ativo, sem inicialização automática/testes ignorados. Restore/build/testes/publish, SAM para ambos e actionlint/ShellCheck deverão preceder publicação.

Evidências LA.1 antigas não certificam AE. Nome do repositório, pastas, solução e caminhos auth foram migrados na AE.2, preservando código funcional e recursos atuais. O prazo de até quatro dias de calendário considera jornadas maiores que oito horas e a estimativa de 32–48 horas; resultados locais e AWS serão registrados separadamente. Não há autorização de execução remota nesta documentação.


## Contratos consolidados — AE.1

[Contrato central HTTP/JWT/Invoke e dependências](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/CONTRATOS_EXTRA_AUTENTICACAO_E_EMAIL.md), 22/10/2026. Código de oito dígitos/5 minutos/5 tentativas; solicitação limitada também para documentos desconhecidos, cooldown de 60 segundos e cinco pedidos/hora. Pending só autentica depois de virar Ready após captura confirmada. JWT Customer genérico por dez minutos; ClockSkew zero na API. Respostas 202/200/400/401/429/500 e envio v1 Captured/failure definidos. Código LA descartado pelo mantenedor; evidências anteriores são históricas. AE.2 organizou os projetos localmente e preservou o funcionamento auth; renomeação remota e auth-ci/OIDC estão comprovados; plano final aprovado/aplicado, confiança antiga retirada e Terraform posterior sem diferenças. AE.2 concluída. Nenhum fluxo de código/e-mail foi implementado.

## AE.2 — validação local

Restore/build Release sem avisos/erros e 269 testes existentes aprovados, sem falhas/ignorados, incluindo PostgreSQL real em Testcontainers. SAM validate/build, publish auth linux-x64, estrutura email isolada e actionlint/ShellCheck aprovados. C# movido sem alteração de conteúdo, OpenAPI idêntico; somente CodeUri foi ajustado no template auth. Terraform da transição validado offline. [Roteiro e pendências administrativas](MIGRACAO_AE2_SERVERLESS.md). Evidências locais em artifacts/ae2/validation.json e artifacts/ae2/tests; não comprovam deploy ou envio.
