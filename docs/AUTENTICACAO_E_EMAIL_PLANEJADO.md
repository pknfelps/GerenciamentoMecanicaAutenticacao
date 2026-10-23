# Autenticação e envio centralizado — organização planejada

**22/10/2026 — AE.1–AE.6 preparadas/validadas nos seus escopos; integração da API e publicação ainda pendentes.** O [plano central AE.1–AE.10](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTENTICACAO_E_EMAIL.md) substitui o Authorizer e o token por OS. GitHub/raiz/remote já se chamam GerenciamentoMecanicaServerless; autenticação atual preservada.

## Organização e responsabilidades

- Nome atual: GerenciamentoMecanicaServerless. Function/FunctionTests já foram movidos localmente para AuthFunction/AuthFunctionTests na AE.2. Preservar Domain/Application/Infrastructure da autenticação, sem obrigar renomeação ampla de assemblies/namespaces ou de recursos AWS existentes.
- EmailFunction/EmailFunctionTests têm composição/handler/transporte implementados e validados localmente na AE.5, com handler/ZIP/role/logs separados e sem referência ao domínio ou banco da autenticação. Uma solução e um repositório não significam execução ou privilégios compartilhados.
- templates/auth.yaml administra somente a stack auth atual; templates/email.yaml foi preparado na AE.5 para a stack email; integração aos workflows na AE.8. Configurações de deploy separadas; alteração/destroy de uma não administra a outra.
- AuthFunction valida cliente, desafios e JWT e monta a mensagem do código. EmailFunction realiza o transporte SMTP de mensagens produzidas por API/auth. Não implementar SMTP nas duas origens, nem criar endpoint público de envio.

## Fluxo do cliente e captura

POST /customers/auth/request-code recebe document, cria desafio para cliente Active e chama envio síncrono por IAM; responde genericamente com challengeId, sem código. POST /customers/auth/verify-code recebe challengeId/code/document e valida cadastro/estado/expiração/tentativas/consumo para emitir JWT Customer de cliente, sem order_id e sem documento. AE.1 consolidou respostas/limites; implementar conforme o contrato central, ainda sem endpoints novos.

O cliente inicia o fluxo; o mecânico não emite acesso. O operador consulta o e-mail no SMTP4dev por acesso privado ao cluster para simular recebimento. A captura não comprova acesso à caixa pessoal de e-mail. SMTP4dev não fará relay externo e não terá volume persistente. Desafios ficam no PostgreSQL; caixa perdida exige novo código, sem endpoint que recupere seu valor.

Auth precisa de SELECT de email e grants restritos sobre as tabelas de desafios e limites por UUID do cliente existente, sem acesso a orders/usuários internos ou admin. Email não recebe credencial SQL/JWT. Persistir verificador protegido, limitar solicitações/tentativas, invalidar reenvio anterior e consumir uma única vez atomicamente. Falhas de envio/bootstrap/SQL permanecem técnicas e não expõem conteúdo.

JWT válido é usado diretamente na API, sem chamada prévia de sessão ou envio automático em 401. A API confere Active/titularidade, preserva Mechanic e aplica estado/transação da decisão. A rota antiga customers/validate e o Authorizer devem ser removidos/adaptados sem manter bypass por CPF ou token de orçamento.

## Publicação e reutilização

Auth e email terão CI/publish/provision/destroy próprios. Compartilhar por workflow_call setup/build/package, checksum/VersionId imutável, OIDC/deploy e diagnósticos realmente equivalentes; manter templates/roles/prefixos/testes/bloqueios específicos e entradas validadas. Não aceitar shell arbitrário como parâmetro nem confundir separação de YAML com isolamento IAM.

Jobs reutilizáveis selecionam Environment; preservar política branch/ambiente e aprovações atuais. Fixar revisão do código/artefato, checar arquivos e versão exata de cada ZIP. CI deve cobrir mudanças compartilhadas e emitir os required checks, que serão revisados na migração.

Gateway continua usando versão numérica da auth. API/auth usam alias estável da função email por ambiente, com grants restritos e versão auditável; publicar correção de SMTP não exige republicar consumidores somente por troca do número. Catalogar esse consumo separadamente do auth/v2 existente.

Antes de renomear GitHub/local, preparar confiança OIDC de transição no bootstrap por plano autorizado, revisar links/remotes/uses, variáveis/Environments, escopo dos artefatos e checks. Atualizar CodeUri, solução e caminhos de testes/publish. Retirar confiança antiga após migração; não renomear automaticamente stacks/função auth nem expor segredos em arquivos.

## Dependências, testes e limites

Base/SMTP4dev precede email; banco inicializado e email precedem auth/API; Gateway vem depois. No descarte, Gateway → API/auth → banco/email → base. Auth-destroy mantém seu bloqueio Gateway; email-destroy exige API/auth ausentes. Dois SAMs permitem retirar auth sem apagar o envio ainda consumido pela API.

Manter SDK/runtime, bootstrap reutilizado, TLS do RDS, deadlines, sanitização e observabilidade existentes. AE.1 definiu SAM local start-lambda/Invoke v1 e SMTP4dev do Compose, sem duplicar SMTP na API; auth mantém bootstrap injetado nos testes. A execução Docker será comprovada na implementação.

Testar desafios/claims/titularidade, reuso/concorrência/cancelamento, efeitos de falhas SQL/Invoke/SMTP, consumo único, ausência de dados sensíveis e mensagens de API/auth na captura. Docker deve estar ativo, sem inicialização automática/testes ignorados. Restore/build/testes/publish, SAM para ambos e actionlint/ShellCheck deverão preceder publicação.

Evidências LA.1 antigas não certificam AE. Nome do repositório, pastas, solução e caminhos auth foram migrados na AE.2, preservando código funcional e recursos atuais. O prazo de até quatro dias de calendário considera jornadas maiores que oito horas e a estimativa de 32–48 horas; resultados locais e AWS serão registrados separadamente. Não há autorização de execução remota nesta documentação.


## Contratos consolidados — AE.1

[Contrato central HTTP/JWT/Invoke e dependências](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/CONTRATOS_EXTRA_AUTENTICACAO_E_EMAIL.md), 22/10/2026. Código de oito dígitos/5 minutos/5 tentativas; cliente inexistente retorna antes de qualquer gravação, com log sanitizado; limites para cliente existente elegível usam seu UUID, cooldown de 60 segundos e cinco pedidos/hora. Pending só autentica depois de virar Ready após captura confirmada. JWT Customer genérico por dez minutos; ClockSkew zero na API. Respostas 202/200/400/401/429/500 e envio v1 Captured/failure definidos. Código LA descartado pelo mantenedor; evidências anteriores são históricas. AE.2 organizou os projetos localmente e preservou o funcionamento auth; renomeação remota e auth-ci/OIDC estão comprovados; plano final aprovado/aplicado, confiança antiga retirada e Terraform posterior sem diferenças. AE.2 concluída. Nenhum fluxo de código/e-mail foi implementado.

## AE.2 — validação local

Restore/build Release sem avisos/erros e 269 testes existentes aprovados, sem falhas/ignorados, incluindo PostgreSQL real em Testcontainers. SAM validate/build, publish auth linux-x64, estrutura email isolada e actionlint/ShellCheck aprovados. C# movido sem alteração de conteúdo, OpenAPI idêntico; somente CodeUri foi ajustado no template auth. Terraform da transição validado offline. [Roteiro e pendências administrativas](MIGRACAO_AE2_SERVERLESS.md). Evidências locais em artifacts/ae2/validation.json e artifacts/ae2/tests; não comprovam deploy ou envio.


## Revisão do mantenedor — AE.3

Tabelas customer_auth_challenges/customer_auth_request_limits diretamente no Init.sql para banco vazio; sem migrations. O destroy do ambiente remove a instância RDS, e novo provisionamento recria os dados a partir do SQL. Nenhum CHECK de negócio, UNIQUE condicionado ao estado, FK nova de cliente ou cascata; regras de validade, tentativas, estados e consumo são implementadas/validadas em C#, com transações e operações condicionais.

Auth consulta o cliente antes de qualquer gravação. Se ausente, registra somente mensagem fixa sanitizada e retorna imediatamente o 202 genérico com identificador opaco em memória; não persiste desafio, limite ou auditoria nem executa envio/limpeza. Para cliente existente elegível, limites usam customer_id; não há HMAC/HKDF do documento para o índice. Código continua protegido por PBKDF2-SHA256. Esse fluxo será implementado na AE.6; o ajuste atual consolida o contrato e o DDL.

## AE.3 — operações transacionais preparadas

Componentes C# de criação/substituição Pending, ativação Ready, invalidação e confirmação/consumo preparados em ICustomerAccessStore/PostgreSqlCustomerAccessStore, com regras em CustomerAccessChallenge/CustomerAccessRequestLimit. Cliente inexistente retorna antes de gerar código ou acessar as tabelas de autenticação, registrando somente mensagem Information fixa. Limites por UUID do cliente encontrado, 60 segundos/cinco pedidos por hora; negativas não adiam o cooldown.

Advisory lock transacional por cliente, locks das linhas novas e relógio UTC do PostgreSQL; sem FOR UPDATE/escrita em customers, constraints de negócio, migrations ou transação aberta durante envio. Tentativa incorreta/documento divergente de Ready válido é persistida com commit; quinta invalida. Assinatura ocorre antes do consumo/commit e token só retorna após confirmação. Substituído/encerrado não é reativado. Cancelamento/falha provoca rollback preservando a exceção original.

Restore/build Release, regressão de 309 testes (40 novos: 11 regras C# e 29 PostgreSQL), zero falhas/ignorados. Testes locais usam PostgreSQL 16.15/Testcontainers e grants explicitamente preparados na fixture; não substituem a [conferência separada do SQL real de grants](https://github.com/pknfelps/GerenciamentoMecanicaBancoDados/blob/develop/docs/AE3_PERMISSOES_AUTENTICACAO_2026-10-22.md), concluída localmente. Detalhes, nomes de testes e limites: [operações transacionais](https://github.com/pknfelps/GerenciamentoMecanicaServerless/blob/develop/docs/AE3_OPERACOES_TRANSACIONAIS.md).

O store ainda não está ligado ao handler/casos de uso HTTP, e não chama EmailFunction. Isso permanece na AE.6. Resultado de criação contém código/e-mail somente para uso interno e não deve ser serializado ou registrado no HTTP/log; a resposta pública será montada separadamente. Grants preparados e validados localmente; [Limpeza oportunista implementada e validada](AE3_LIMPEZA_EXPIRADOS.md), sem retenção extra de 24 horas e com até 100 expirados por tabela após commit. AE.3 concluída no escopo local; regressão completa final de 328 testes aprovada. Nenhuma alteração AWS, SQL/grants, IAM ou workflows nesta preparação.

## AE.4 — preparação IAM do envio

[Implementação e limites](https://github.com/pknfelps/GerenciamentoMecanicaInfraestrutura/blob/develop/docs/AE4_IAM_EMAIL.md): runtime email e pipeline próprios por ambiente no bootstrap; API/auth recebem descoberta do contrato email/v1 e Invoke somente do alias live. O deploy email usará AWS_EMAIL_ROLE_ARN do Environment hom/prd, depois de apply aprovado e cadastro autorizado; não usará a role de deploy auth. A runtime email não recebe banco/JWT/Secrets nem lookup SSM por mensagem. Templates/workflows da função seguem na AE.5/AE.8; nenhum deles foi alterado nesta preparação. Não houve plano/apply, configuração GitHub ou chamada AWS autenticada. A role de CI existente permanece inalterada.

## AE.5 — EmailFunction preparada e validada localmente

[Implementação, comandos e evidências](AE5_EMAIL_FUNCTION.md). Contrato Invoke v1 estrito, transporte MailKit central, remetente fixo, budget de três segundos com reserva de resposta, erros/logs sanitizados e template SAM privado com alias live/catálogo email/v1. Nenhuma dependência de banco/JWT/auth no ZIP email. Restore/build Release, 419 testes (91 novos), SAM validate/build de ambos e pacotes linux-x64 conferidos. Primeira invocação isolada, reutilização, entrada inválida, SMTP indisponível e captura/perda após restart do Compose comprovados localmente; sem relay externo. Compose da API agora usa caixa em memória, sem alterar PostgreSQL.

AE.5 concluída no escopo local; API/AuthFunction ainda não invocam EmailFunction. Próximo passo: AE.6, request-code/verify-code com store e Invoke. AE.7 conecta API, AE.8 integra publicação/workflows/Gateway e AE.10 comprova AWS após autorização. Não houve implantação, mudança de IAM/banco ou execução remota nesta etapa.

## AE.6 — autenticação por código preparada e validada localmente

[Contrato, composição e evidências](AE6_AUTENTICACAO_POR_CODIGO.md). Duas rotas estritas, sem emissão por documento isolado; store AE.3 integrado ao Invoke v1 da AE.5. Pending persistido antes do envio, Ready condicional após captura, invalidação best effort, confirmação/consumo atômicos e JWT atual sem dados extras. Descoberta em dois lotes SSM (9+2), alias do ambiente/contrato 1, MaxErrorRetry=0 e sem overrides de endpoint AWS. Mesmos limites de bootstrap, deadline e flush; nomes das duas rotas no manifesto e categorias code-requested/rate-limited.

Restore/build Release sem avisos/erros, 438 testes sem falhas/ignorados, publish Linux/SAM de ambas conferidos. Fluxo handler/PostgreSQL/EmailFunction/SMTP4dev/confirmar/JWT comprovado localmente com AWS injetada, incluindo concorrência, erros e sanitização. O OpenAPI Serverless agora é 2.0.0; publicar coordenadamente com Gateway na AE.8. API permanece para AE.7. Sem SQL, IAM, workflows, planos/aplicações ou chamadas AWS nesta etapa.

## AE.7 — integração da API comprovada localmente

[API: transporte e acesso Customer](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/AE7_API_ENVIO_E_ACESSO_CUSTOMER.md) concluídos: Invoke em todos os modos sem SMTP próprio/simulação, cadastro Active/titularidade, ClockSkew zero e decisão 200 com approved explícito. 1.212 testes da API aprovados; caminho real API container/SDK/SAM/EmailFunction/SMTP4dev comprovado, inclusive falha de notificação sem desfazer criação persistida. Contratos do Serverless e código das funções não foram alterados nesta etapa. Próximo passo AE.8: publicação/workflows/Gateway, seguido do roteiro conjunto AE.9 e comprovação AWS autorizada AE.10.

## AE.8 — preparação concluída localmente, 22/10/2026

[Entradas e reutilizáveis auth/email, imutabilidade, dependências e Gateway](AE8_WORKFLOWS_E_GATEWAY.md). SAMs hom/prd, lint, Terraform e 438 testes aprovados. Proteções/identidade GitHub reconferidas em somente leitura; checks obrigatórios preservados. Sem workflow remoto ou aplicação AWS. Próxima etapa AE.9; publicação integrada permanece AE.10.

## AE.9 — conjunto conferido localmente, 23/10/2026

[Comprovação e documentos atuais](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/AE9_VALIDACAO_LOCAL_E_DOCUMENTACAO.md). Postman/contratos/diagramas alinhados; 47 conferências manuais conjuntas e regressões de 438 Serverless/1.212 API aprovadas. Transporte real SDK/SAM/SMTP4dev; bootstrap auth injetado, sem fallback produtivo ou novo executável. OTP/JWT em memória; projeto descartável removido. Publicação/IAM/rede/runtime AWS continuam na AE.10; nenhum fluxo remoto foi executado.
