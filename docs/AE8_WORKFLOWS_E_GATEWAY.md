# AE.8 — Workflows das funções e Gateway

**22/10/2026 — preparado e validado localmente.** Nenhum workflow remoto, plano/apply/deploy/destroy AWS ou alteração de configuração GitHub executado. AE.9 consolida Postman, diagramas e roteiro conjunto; AE.10 comprova a implantação após autorização.

## Entradas e reaproveitamento

| Entrada auth | Entrada email | Implementação comum |
|---|---|---|
| auth-ci | email-ci | reusable-ci.yml |
| auth-publish | email-publish | reusable-publish.yml |
| auth-provision | email-provision | reusable-provision.yml |
| auth-destroy | email-destroy | reusable-destroy.yml |

Os wrappers fixam component=auth/email. Somente environment=hom/prd e, no provision, artifact_commit completo são entradas operacionais. Template, logical ID, stack, prefixos, configuração SAM e role derivam de uma allowlist conferida antes de acesso AWS; não há input de comando shell, bucket, endpoint ou ARN. Workflow reutilizável local é obtido do mesmo commit do chamador. Actions externas permanecem fixadas por SHA.

Hom executa em develop; prd em main. Cada componente mantém sua fila (auth-ENV/email-ENV), sem cancelamento. Os jobs de publicação/provision/descarte selecionam o Environment hom/prd; não usam hom-approval/prd-approval nem acrescentam aprovação. A execução manual continua autorizando a operação completa. O workflow precisa existir na main para aparecer no disparo manual.

CI de ambos roda em todo PR para develop/main, sem filtros de caminho, cobrindo solução completa, PostgreSQL/SMTP4dev, os dois SAMs e todos os workflows. Usa somente a role CI existente para os dois objetos Auth.Contracts/hash, nunca roles runtime/deploy ou Secrets. Auth-ci preserva os checks obrigatórios de nome **dotnet** e **configuration** como gates finais sem OIDC: dependem do resultado integral do reutilizável e falham se ele falhar/cancelar. Os jobs internos recebem nomes qualificados; não substituem os dois checks exigidos pelo ruleset. Nenhuma suíte ou mock foi criada.

## Publicação e artefatos

Package faz restore/build/testes e SAM build da função escolhida. Verifica DLL/deps/runtimeconfig e isolamento das dependências: Auth contém certificado RDS e dois arquivos OTel, sem MailKit/EmailFunction; Email contém MailKit, sem Npgsql/Auth/testes. O build passa ao job publish pelo ID exato do artefato GitHub da mesma execução, com sete dias de retenção. OpenAPI só acompanha auth.

Publish usa AWS_ROLE_ARN (auth) ou AWS_EMAIL_ROLE_ARN (email), exigindo ARN exato da role do ambiente/componente. Não usa fallback de autorização: variável ausente/divergente encerra a validação antes de OIDC.

| Objeto | Auth | Email |
|---|---|---|
| ZIP nativo SAM | sam/auth/ENV/COMMIT/ | sam/email/ENV/COMMIT/ |
| Template/checksum | contracts/auth/COMMIT/sam-ENV.json[.sha256] | contracts/email/COMMIT/sam-ENV.json[.sha256] |
| OpenAPI/checksum | contracts/auth/COMMIT/openapi.json[.sha256] | Não possui endpoint/contrato público |

SAM package continua nativo. Depois, publica-se CodeUri como objeto **Bucket/Key/Version**, com VersionId real não vazio, antes do checksum. Contratos/templates/sidecars usam If-None-Match: *; somente PreconditionFailed permite comparar o objeto já existente. Republicação idêntica conserva a versão original após comparar bytes do ZIP e do template. Divergência/formato antigo exige commit novo. A chave do ZIP é gravável/versionada; fixar a versão no template impede trocar o código apenas criando uma versão corrente nova.

Provision confere ancestralidade do artifact_commit, checksum, função/runtime/alias/role esperados e ZIP no prefixo ambiente/commit. HeadObject usa --version-id e exige a mesma versão/tamanho positivo. Aplica o template publicado; não recompila o código nem muda Version. Resumo registra revisão, chave/VersionId, nome/versão/ARN da função; não registra conteúdo de mensagens ou credenciais.

## Pré-requisitos e configuração

1. Aplicar bootstrap e base preparados nas AE.4/AE.8 somente por plano salvo aprovado. A base precisa ter SMTP4dev, listener privado e catálogo base/v2.
2. Manter AWS_CI_ROLE_ARN no repositório. Nos Environments hom/prd, manter AWS_ROLE_ARN da auth e cadastrar **AWS_EMAIL_ROLE_ARN** com o output email_github_environment_variables do bootstrap aplicado.
3. Email: email-publish → email-provision com o commit publicado. Resolve base/v2/vpc-id, workload-subnet-ids, email-security-group-id, nlb-security-group-id, smtp-host e smtp-port. Confere VPC/tags, duas subnets privadas/AZs distintas e SGs nomeados do ambiente, DNS NLB e porta 2525. Não acessa RDS, JWT, Secrets ou layer. Usa samconfig.email.toml e stack mecanica-ENV-email.
4. Banco/database-init e email podem ser provisionados independentemente após a base. Auth só vem depois de ambos: auth-publish → auth-provision. Preserva inspeção da layer e metadados AWSCURRENT New Relic, sem ler valores. OTel/X-Ray continuam sempre ativos na auth. SMTP não é acrescentado à auth.
5. Antes de implantar API/auth, conferir email/v1/invocation-arn exato do alias live e contract-version=1. Os guards verificam metadados, **não comprovam conectividade SMTP nem saúde da função**. O runtime também valida o contrato no bootstrap. Falhas de consulta bloqueiam; catálogo ausente não é tratado como lista vazia.
6. Publicar/implantar API e atualizar Gateway com os contratos novos e versão numérica auth. Não usar isoladamente o Gateway antigo: o handler novo já removeu customers/validate.

Não há envio público da EmailFunction. Sua stack publica alias live/versão e cinco campos email/v1. Auth continua publicando seis campos auth/v2; auth-contracts-version=1.0.0 identifica o pacote JWT compartilhado, **não** a versão HTTP OpenAPI 2.0.0. Gateway continua usando a versão numérica auth, enquanto API/auth invocam o alias email live.

## Gateway

Gateway exige OpenAPI auth 2.0.0 e exatamente POST /customers/auth/request-code + POST /customers/auth/verify-code. Usa AWS_PROXY para a versão numérica indicada em auth/v2. Duas permissões Lambda específicas limitam principal apigateway.amazonaws.com, conta, stage, método e caminho; não há wildcard de rota, Authorizer ou exposição da EmailFunction. A permissão antiga de customers/validate será retirada pelo plano Gateway; revisar essa substituição antes do apply.

HTTP_PROXY da API, JWT no backend, probes privadas, logs sanitizados e tracing existentes permanecem preservados. Gateway-provision mantém plan → aprovação → apply do plano salvo; não ganhou inputs ou comandos novos.

## Descarte

**Gateway → API/auth → banco/email → base/SMTP4dev.** API/auth podem ser descartados independentemente depois do Gateway. Banco e email podem ser descartados independentemente após seus consumidores.

- Auth-destroy: checa REST API/VPC Link do Gateway por nome exato; sam delete somente de auth; confere ENIs/egress do próprio SG.
- Email-destroy: consulta Lambda ListFunctions para excluir auth; confirma ausência do cluster EKS ou consulta no namespace default Deployment/HPA/ServiceAccount da API e pods/ReplicaSets app=gerenciamento-api. Qualquer recurso, inclusive réplicas zero/pod terminando, bloqueia. Depois executa sam delete somente de email e confere ENIs/egress do SG email.
- SG isolado pertence à base e é permitido. ENI/egress pendentes causam falha imediata; aguardar externamente e repetir. Erro AWS/kubectl/JSON falha fechado. Sem exclusão manual de interfaces ou espera automática.
- Banco-destroy mantém API/auth como consumidores, sem dependência artificial email. Base-destroy já exige banco e email ausentes.

SAM usa configurações e prefixos changesets separados. CodeUri objeto preserva ZIP no sam delete 1.167.0; contratos, versões S3, bucket, roles e SG da base persistem. Não há DeleteObjectVersion ou remoção de contratos, outra stack, banco ou base.

## IAM e proteções

Revisão AE.8 completou os escopos preparados: contratos email Get/Put/GetObjectVersion somente em contracts/email/*; pipeline API/auth lê somente invocation-arn e contract-version de email/v1 do próprio ambiente; trust email ganhou a mesma condição ref develop/hom ou main/prd já utilizada nas pipelines. Sem Invoke adicional nas pipelines, acesso a valores Secrets ou escrita extra em IAM/EC2.

As roles têm permissões próprias, mas compartilham o subject do mesmo repositório/Environment. **Isso não isola workflows por arquivo**: um workflow autorizado naquele Environment pode solicitar suas roles. Não foi introduzido job_workflow_ref, novo Environment ou mudança da customização OIDC. Roles runtime continuam separadas e o template email não cria IAM.

Conferência pública somente leitura em 22/10: repository ID 1375105446, nome GerenciamentoMecanicaServerless, público/main; ruleset 23791642 ativo cobre main/develop e exige configuration/dotnet do GitHub Actions. Hom permite somente branch develop; prd somente main. Environments de aprovação existentes não são usados por esses fluxos. Nenhuma configuração remota foi alterada; valor/presença de AWS_EMAIL_ROLE_ARN e trust implantada ainda serão conferidos após o bootstrap autorizado.

## Evidências locais e limites

- Restore/build Release: zero avisos/erros. **438 testes aprovados**, zero falhas/ignorados (Application 32, Function 74, Contract 10, Email 91, Infrastructure 231), com Docker ativo. Relatório local ignorado: artifacts/ae8/test-results/ae8-regression.trx.
- SAM validate --lint/build auth/email; transformação SAM nativa nas quatro combinações função/ambiente preservou S3ObjectVersion, runtime e alias; nenhum recurso público Lambda Permission/Function URL gerado. Certificado/configurações/dependências conferidos.
- Actionlint 1.7.12/ShellCheck 0.11.0: todos os workflows Serverless e infraestrutura e api-deploy aprovados, sem supressões.
- Terraform bootstrap/Gateway: fmt -check, init -backend=false com lockfile readonly e validate aprovados. Console sem provider/backend renderizou políticas reais: email-sam 2.197 caracteres/ambiente (limite 6.144), bootstrap-access API 1.594 e auth 986 (limite inline 10.240). Escopos regionais/ambiente e ausência de escrita adicional conferidos; não é simulação IAM remota.
- Gateway renderizado pelo Terraform com os contratos reais: 27 caminhos hom/prd, duas rotas AWS_PROXY, sem customers/validate, probes ou Authorizer. Não representa recurso provisionado.
- Expressão jq de identidade/role/runtime avaliada sobre os dois templates SAM realmente gerados. Checksum/VersionId e condicionais de publicação foram preservados no mecanismo existente, sem executar chamadas AWS ou criar mocks/scripts de teste.

Publicação, autorização IAM real, execução dos reutilizáveis no GitHub, captura SMTP pela rede AWS e comportamento dos bloqueios reais permanecem para AE.10. Validação local de hom/prd não comprova operação desses ambientes. Não executar workflows ou destroys apenas para testar esta entrega.

Referências: [workflows reutilizáveis](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows), [CodeUri com versão](https://docs.aws.amazon.com/serverless-application-model/latest/developerguide/sam-property-function-functioncode.html), [plano extra](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTENTICACAO_E_EMAIL.md).
