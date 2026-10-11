# Publicação, provisionamento e descarte da autenticação

## Preparação

Publicar os workflows na branch padrão main pelo fluxo normal de PR. Primeira execução: develop/hom; produção: main/prd. Não há deploy por push.

1. Revisar/aplicar o plano salvo do bootstrap que cria mecanica-auth-ci-github. Runtime IAM/deploy hom/prd já foram aplicados na parte 5.
2. AWS_CI_ROLE_ARN é variável do repositório, com o output auth_ci_role_arn; CI não usa Environment.
3. hom/prd usam AWS_ROLE_ARN conforme output do bootstrap. Região, bucket de artefatos e backend estão fixados nos workflows/arquivos Terraform. AWS_REGION, TF_STATE_BUCKET e ARTIFACTS_BUCKET existentes não são consultados por estes workflows.
4. hom-approval/develop e prd-approval/main exigem reviewer pknfelps, permitem autorrevisão e não recebem secrets/variáveis AWS. Esses Environments e AWS_CI_ROLE_ARN já foram configurados/conferidos nesta entrega; criação da role CI continua pendente de apply aprovado.
5. Recriar base e banco, concluir o Job SQL e preservar os parâmetros públicos SSM v2.
6. Resolver a quota Lambda antes do provisionamento: conferência de 2026-10-09 retornou limite total/não reservado 10. Reserva cinco exige pelo menos 105 no total sem outras reservas (110 para hom/prd simultâneos, sem outras reservas). A tentativa autorizada de solicitar 110 foi rejeitada pela API, que exige valor maior que o padrão de 1.000; a API de suporte exige assinatura Premium. O mantenedor pediu para deixar a solicitação pendente. Nenhuma quota foi alterada; não executar o primeiro provisionamento enquanto esse pré-requisito não estiver resolvido.

Environment de aprovação inexistente pode ser criado automaticamente sem proteção. Não usar pull_request_target. PRs de forks não recebem acesso ao pacote AWS: revisar e trazer para uma branch deste repositório antes da suíte completa.

## CI

auth-ci roda em PRs para develop/main e manualmente nessas branches. Job .NET usa OIDC somente de leitura para obter Auth.Contracts [1.0.0] e seu sidecar, confere SHA-256 fixo b9080cea9d303722ac072e89421505d728126cdb52d57549da4328b1e18ab4e9 e isola o cache pelo hash. Executa restore/build Release e todos os testes, incluindo PostgreSQL 16.15 local descartável. Docker indisponível é erro; testes não são ignorados.

Outro job valida Terraform sem backend e os quatro workflows, sem AWS. Terraform 1.15.9, .NET 10 e actionlint 1.7.12; actions fixadas por SHA. Não há validação OpenAPI nem instalação de Node/Redocly. A conferência inicial limita-se à origem do PR ou à branch do disparo manual; confiança OIDC/IAM e allowed-account-ids restringem o acesso à conta. PRs não recebem role de deploy nem acesso a valores de Secrets. Os scripts PowerShell operacionais foram removidos; testes .NET preservados.

## Publicar

Disparar auth-publish com environment. O SHA da execução identifica os artefatos.

Job package confere develop/hom ou main/prd uma vez no início, baixa/verifica o pacote com role CI, compila/testa e publica Function linux-x64 sem self-contained. OpenAPI segue como arquivo estático, sem etapa de lint. Confere DLL/deps/runtimeconfig e Certificates/rds-ca.pem. ZIP contém arquivos na raiz correta, ordem estável e timestamps normalizados com zip -X. Bundle passa ao job publish pelo ID de artefato da mesma execução, retido sete dias.

Job publish usa a role auth do Environment e publica no bucket compartilhado:

| Objeto | Conteúdo |
|---|---|
| lambda/<commit>/function.zip | Código/dependências |
| contracts/auth/<commit>/openapi.json | Contrato estático do mesmo commit |
| Os dois objetos acompanhados de .sha256 | Hash hexadecimal minúsculo de 64 caracteres seguido de LF |

Dois steps separados publicam ZIP/checksum e OpenAPI/checksum. Antes do upload, conferir os hashes do bundle recebido da mesma execução. AWS CLI calcula o checksum com --checksum-algorithm SHA256 e usa If-None-Match: *; o S3 valida e armazena esse checksum. Versões S3 vêm da resposta de upload ou download na reutilização, sem head-object posterior. Objeto existente é comparado byte a byte: idêntico é reutilizado; diferente encerra o fluxo. Outros erros não são tratados como existência. Repetição de publicação parcial reutiliza objetos confirmados e completa os restantes. Mudança de SDK/dependências que altere os bytes exige novo commit/publicação, sem substituir o artefato anterior. Resumo contém commit, hashes e versões S3.

Publicar não implanta a Lambda e não exige aprovação Terraform. Artefatos são compartilhados entre hom/prd.

## Provisionar

auth-provision recebe environment e artifact_commit: SHA completo de 40 caracteres minúsculos, já publicado e presente no histórico da branch selecionada. SHA da execução fixa Terraform; artifact_commit fixa função/contrato. Eles podem diferir. Hash Base64 não é input manual.

- Plan confere branch/ambiente e ancestralidade do commit, lê ChecksumSHA256 do ZIP com head-object --checksum-mode ENABLED e fornece zip_sha256_base64 ao Terraform. Não baixa ZIP/OpenAPI/sidecars novamente. Terraform valida formato do commit/hash, consulta os objetos ZIP/OpenAPI e fixa a versão S3 do ZIP. Executa init/validate/plan -out=tfplan. Plano textual completo aparece no log e no artefato retido sete dias, junto do binário.
- Aprovação usa o Environment protegido, sem checkout, OIDC ou AWS.
- Apply faz checkout do mesmo SHA, nova autenticação, download pelo ID do plano da mesma execução e apply do binário. Sem recompilação ou novo plan. Estado alterado, artefato expirado ou plano inválido exige nova execução completa para revisão.

Quota é pré-requisito administrativo da preparação, sem consulta automática repetida no plan/apply. Conferir o limite e outras reservas com aws lambda get-account-settings antes do primeiro provisionamento e quando a capacidade da conta mudar. Terraform mantém reserva 5; a AWS rejeita apply sem capacidade suficiente, que pode deixar uma implantação parcial. Não retirar automaticamente essa proteção.

Após apply, apresentar os outputs públicos Terraform, sem repetir comparações de função/SSM por scripts. Conferência funcional e inspeção de versão/hash/concorrência ficam na validação real posterior. Em falha, diagnóstico limitado de estado/logs; sem rollback automático.

Os três workflows manuais compartilham concurrency auth-<ambiente>, cancel-in-progress=false. Outros repositórios continuam coordenados pelo mantenedor. Cancelamento Terraform pode deixar lock: investigar a execução antes de force-unlock.

## Descartar

auth-destroy recebe environment e artifact_commit de um artefato publicado. Commit/hash ainda são exigidos pela unidade Terraform: o workflow lê somente o checksum do ZIP no S3, sem validar novamente o bundle nem comparar com auth/v2/zip-key. Preferir o commit implantado; ele preenche os inputs e não seleciona os recursos a apagar. O backend do ambiente e o plano salvo determinam o descarte. Limpeza parcial não depende da existência de zip-key no SSM. Remover esses inputs do destroy exigirá ajustar também o Terraform, fora desta simplificação.

Plan -destroy salvo → aprovação → apply do binário, sem build/upload/replan ou exclusão de artefatos S3. Base/banco/SSM e artefatos devem permanecer disponíveis até concluir destroy auth, pois data sources continuam necessários.

Ordem: Gateway/consumidores → auth/API → banco → base. Auth remove função/versões/logs/auth-v2/dois egress; preserva runtime IAM, SG/subnets da base, artefatos e outro ambiente. DeleteFunction pode terminar antes da limpeza das ENIs. Com perfil administrativo autorizado, consultar SG e interfaces antes de retirar banco/base:

~~~powershell
aws ssm get-parameter --name /mecanica/hom/base/v2/auth-security-group-id --query Parameter.Value --output text
# Usar o SG retornado; para prd, consultar o parametro correspondente.
aws ec2 describe-network-interfaces --filters Name=group-id,Values=sg-XXXXXXXX --query 'NetworkInterfaces[].{Id:NetworkInterfaceId,Status:Status,Description:Description}'
~~~

Aguardar liberação pelo serviço, sem apagar ENIs gerenciadas manualmente. Role/permissões persistem no bootstrap para a limpeza posterior. Essa conferência é procedimento do mantenedor; o workflow não verifica ENIs nem inclui um step que apenas escreva esse lembrete.

## Validação real posterior

Após implantação manual em hom, conferir estado Active/Successful, versão numérica, hash do código, reserva cinco e os parâmetros auth/v2 com ferramentas padrão. Em seguida, invocar a versão numérica com AWS CLI e evento REST proxy, Content-Type application/json:

- Corpo/documento inválido: 400.
- Documento válido sem cliente: 401.
- Cliente elegível do seed, CPF 662.119.730-63: 200 com token string JSON.
- Conferir FunctionError e statusCode: HTTP 200 da operação Invoke sozinho não comprova sucesso do handler.

Guardar resposta apenas em arquivo temporário local e conferir status sem imprimir token. Não anexar respostas de sucesso/JWT aos logs ou artefatos. Gateway não é necessário para essa conferência; endpoint público e aceitação do JWT pela API serão validados depois. Sem teste de carga hospedado ou destroy para testar a implementação.

E3.1/E3.2/E3.6/E3.9 e E2.12 permanecem abertas até suas validações AWS.

Referências: [Environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments), [S3 condicional](https://docs.aws.amazon.com/cli/latest/reference/s3api/put-object.html), [concorrência](https://docs.aws.amazon.com/lambda/latest/dg/configuration-concurrency.html), [ENIs](https://docs.aws.amazon.com/lambda/latest/dg/configuration-vpc.html).

