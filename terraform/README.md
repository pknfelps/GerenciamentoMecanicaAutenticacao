# Terraform da autenticação — partes 5/6

Unidade preparada para hom/prd, conta 121754142617/us-east-1, Terraform ~> 1.15.0 e AWS 6.58.0. Unidade validada localmente e runtime IAM aplicado na parte 5. Workflows implementados na parte 6; nova role CI e primeira implantação da função dependem dos respectivos planos aprovados. Artefatos da função ainda não foram publicados.

## Propriedade e dependências

| Proprietário | Recursos |
|---|---|
| Bootstrap persistente | Role mecanica-<ambiente>-auth-runtime, policy de execução e policy gerenciada da pipeline auth |
| Base | VPC, NAT, duas subnets privadas de workloads, SG reservado mecanica-<ambiente>-auth, JWT e configuração base/v2 |
| Banco | RDS, SG com ingresso PostgreSQL a partir do SG auth, credencial mecanica_auth, esquema e parâmetros database/v2 |
| Auth | Lambda, versões publicadas, log group, duas regras de saída do SG reservado e seis parâmetros auth/v2 |
| Gateway futuro | REST API, integração pela versão numérica e permissão de invocação |

O Terraform auth consulta quatro parâmetros públicos String com with_decryption=false: base/v2/vpc-id, workload-subnet-ids (array JSON), auth-security-group-id e database/v2/security-group-id. Inspeciona VPC/subnets/SGs e role persistente; não consulta estado de outros repositórios nem valores de Secrets. Subnets/SGs são conferidos contra a VPC do ambiente antes de criar as regras de rede.

A função usa dotnet10/x86_64, ZIP, handler implementado, 512 MB, timeout de quinze segundos e concorrência reservada cinco, sem provisionada. Publish=true fornece versão numérica. Runtime__Environment é a única variável própria; AWS_REGION é reservada e fornecida pela Lambda. O log group /aws/lambda/mecanica-<ambiente>-auth tem retenção sete dias e formato JSON. New Relic/OTel permanece na E5.

As regras de saída permitem TCP 5432 exclusivamente para o SG do banco e TCP 443 para IPv4 via NAT existente. Não há ingresso, novo SG, endpoint de interface ou RDS Proxy. O runtime lê uma vez os nove parâmetros do carregador e AWSCURRENT dos Secrets JWT/database-auth. Pipeline e estado Terraform não leem chave, senha ou string de conexão.

## Artefatos necessários

Os inputs environment, artifact_commit e zip_sha256_base64 são obrigatórios. Commit deve ser SHA Git completo, e o hash é SHA-256 do arquivo ZIP em Base64 (não ETag). Não existem defaults de código provisório.

| Objeto no bucket mecanica-artifacts-121754142617-us-east-1 | Uso |
|---|---|
| lambda/<commit>/function.zip | Publish linux-x64 sem self-contained, DLLs/runtimeconfig e Certificates/rds-ca.pem na raiz adequada do ZIP |
| contracts/auth/<commit>/openapi.json | Contrato estático de POST /customers/validate do mesmo commit |

Terraform consulta os objetos existentes, exige conteúdo não vazio e versionamento do ZIP, fixa s3_object_version e registra code_sha256. Não compila, empacota ou faz upload. A publicação futura deve preservar as chaves por commit com escrita condicional, sem sobrescrever artefatos. O hash informado deve corresponder exatamente ao ZIP daquela versão S3.

## Validação local, sem AWS

Comandos na pasta terraform:

```powershell
terraform init -backend=false -input=false -lockfile=readonly
terraform fmt -check
terraform validate
```

Lockfile versionado inclui checksums oficiais para windows_amd64 e linux_amd64. Para atualizar conscientemente o lockfile, usar terraform providers lock -platform=windows_amd64 -platform=linux_amd64.

## Primeiro provisionamento — entrega seguinte

Aplicar primeiro um plano salvo aprovado do bootstrap. Recriar base e banco do ambiente, concluir o Job database-init e publicar ambos os artefatos antes do plan auth. Ler SSM não comprova prontidão do banco nem acesso de rede. Preparar ZIP/contrato e workflows pertence à próxima entrega.

Antes do primeiro apply, consultar a quota e reservas atuais com AWS CLI (get-account-settings, list-functions/get-function-concurrency e Service Quotas). Reservar cinco exige preservar o mínimo de cem execuções não reservadas, considerando outras reservas na região. Se a quota não permitir, resolver antes do apply; não reduzir nem retirar automaticamente reserved_concurrent_executions.

Para o futuro plan em hom, criar auth.tfvars local com environment="hom", artifact_commit e zip_sha256_base64 reais e previamente publicados; não versionar o plano ou esse arquivo. Usar credencial da role auth do ambiente. Em prd, usar backend.prd.hcl e environment="prd". Não usar workspaces de ambiente.

```powershell
terraform init -backend-config=backend.hom.hcl -reconfigure -lockfile=readonly
terraform workspace show
terraform fmt -check
terraform validate
terraform plan -var-file=auth.tfvars -out=auth.tfplan
terraform show -no-color auth.tfplan
# Somente depois da aprovacao deste plano salvo:
terraform apply auth.tfplan
```

O backend usa S3 criptografado, locking nativo use_lockfile=true, conta restrita e chave hom/auth/terraform.tfstate (prd/auth/terraform.tfstate em prd). Não há novo plan depois da aprovação. Alterar base/banco/artefatos exige gerar outro plano auth.

## Configuração publicada

Seis parâmetros String/Standard em /mecanica/<ambiente>/auth/v2/:

| Campo | Valor |
|---|---|
| function-name | mecanica-<ambiente>-auth |
| function-version | Versão numérica publicada |
| invocation-arn | ARN Lambda qualificado, terminando em :<versão>; não é a URI de integração REST |
| zip-key | lambda/<commit>/function.zip |
| openapi-key | contracts/auth/<commit>/openapi.json |
| auth-contracts-version | 1.0.0 |

Gateway construirá a URI de integração a partir do ARN qualificado e administrará sua própria permissão de invocação. Esta unidade não cria API Gateway, Function URL, alias ou aws_lambda_permission e não usa $LATEST para consumidores. Metadados são configuração, sem declaração de prontidão.

## Descarte e validação futura

Ordem: Gateway/consumidores → auth/API → banco → base. Destroy auth remove função/versões, log group, parâmetros auth/v2 e suas duas regras de saída; preserva SG/subnets e a role/policy de execução no bootstrap. A exclusão da função pode retornar antes da liberação de ENIs; aguardar a AWS concluir a limpeza antes de descartar base/rede. Manter a role e suas permissões permite essa limpeza posterior; não remover o bootstrap com o ambiente.

auth-destroy segue plan -destroy salvo → revisão/aprovação → apply do binário. Nesta entrega não houve destroy para testar. Antes de aprovar descarte/recriação, revisar versão S3/commit e alterações de rede; planos precisam mostrar apenas recursos da unidade auth.

Access Analyzer, planos AWS, aplicação IAM, quota, deploy, conexão real TLS e invocação serão conferidos na entrega seguinte. E3.1/E3.2/E3.6/E3.9 e E2.12 continuam abertas.
**2026-10-09 — Bootstrap auth aplicado:** após aprovação explícita do plano salvo, aplicado exatamente o binário bootstrap-auth-runtime.tfplan (SHA-256 522ED5C8F0272A66F5D66D979CF3BE1B953E2DE77EE6925179C1F73C84E30160), com a sessão administrativa existente na conta 121754142617. Resultado: oito criações, zero alterações/exclusões — roles auth-runtime, policies de execução, policies auth-provision e associações em hom/prd. Quatro policies validadas pelo Access Analyzer sem findings; conferência posterior Terraform retornou No changes. Trust Lambda, policy inline e associação à pipeline conferidas nas duas roles. Simulações IAM hom/prd: JWT/auth do próprio ambiente em AWSCURRENT permitidos, database/api e JWT do outro ambiente negados; pipelines auth sem leitura dos dois Secrets. Nenhum valor de Secret foi consultado. allow_legacy_cleanup=false preservado. Evidências locais em Infraestrutura/artifacts/bootstrap-auth-runtime. Função, ZIP/OpenAPI, workflows e Gateway permanecem pendentes; base/banco precisam ser recriados antes do deploy.
## Workflows da parte 6

auth-publish publica artefatos antes do plan. auth-provision/auth-destroy recebem environment e artifact_commit; hash Base64 é lido de ChecksumSHA256 registrado no S3, sem baixar novamente o ZIP. SHA do workflow fixa Terraform, artifact_commit fixa função/contrato. Três jobs: plan com artefato sete dias → Environment protegido sem AWS → apply do plano da mesma execução/commit. Sem replan após aprovação. O workflow confere branch/ambiente somente no início; formato do commit/hash e destino ficam no Terraform. Apply apresenta outputs públicos, e conferência funcional ocorre depois. Destroy ainda exige commit/hash pelos inputs da unidade, sem comparação com o commit no SSM. Requisitos, quota, descarte parcial e ENIs em [procedimento](../docs/DEPLOY.md). Workflows preparados e configuração GitHub conferida; role CI exige novo plano aprovado. Primeira publicação/deploy e validação real continuam pendentes.

Quota Lambda conferida em 2026-10-09: limite total/não reservado 10. Isso impede reservar cinco mantendo cem não reservadas; resolver antes de auth-provision. A tentativa autorizada de solicitar 110 foi rejeitada pela API de quotas, que exige valor maior que 1.000; a API de suporte exige assinatura Premium. Solicitação pendente por decisão do mantenedor, sem alteração da quota. A capacidade é pré-requisito administrativo e não é consultada pelos workflows a cada plan/apply. O plano de IAM CI não cria função e pode ser revisado/aplicado independentemente.
