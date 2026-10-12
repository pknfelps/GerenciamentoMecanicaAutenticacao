# Publicação e operação da autenticação com SAM

## Recursos e pré-requisitos

Auth usa SAM CLI 1.167.0/CloudFormation com ZIP .NET 10/linux-x64. [Template](../template.yaml) declara função/versão/alias, logs, duas saídas do SG e seis parâmetros auth/v2. [samconfig.toml](../samconfig.toml) fixa stacks hom/prd, região e bucket; configure-aws-credentials restringe conta 121754142617. Bootstrap/base/banco/Gateway continuam Terraform; IAM runtime/SG/bucket/contratos e ZIPs de outras revisões ficam fora do descarte auth; o ZIP atual e templates operacionais SAM são removidos.

Conferência somente leitura em 2026-10-11 na conta 121754142617: nenhuma função, stack, log group ou parâmetro auth/v2; nenhum objeto ou versão histórica nos backends hom/auth e prd/auth. As roles runtime/deploy hom/prd existem; a role CI ainda não existe. Os parâmetros/SGs de base/banco estão ausentes. Por isso o código Terraform auth foi retirado sem importação ou operação de destroy; nenhum estado foi apagado.

Antes da primeira execução:

1. Bootstrap CI/SAM delete aprovado/aplicado em 2026-10-11 com Terraform 1.15.9: seis criações, cinco alterações, nenhuma exclusão. Inclui role CI, permissões CloudFormation/alias, upload/exclusão SAM restritos ao ambiente e retirada do acesso ao backend auth. Plano posterior retornou No changes; policies/associações SAM, CI e bucket conferidos. Futuros ajustes do bootstrap continuam administrativos e exigem plano salvo aprovado.
2. Configurar AWS_CI_ROLE_ARN no repositório com o output auth_ci_role_arn; hom/prd usam AWS_ROLE_ARN. Conta 121754142617, região e bucket estão fixados nos workflows.
3. Usar hom/prd para variáveis OIDC, com branches develop/main correspondentes. Os workflows auth não referenciam hom-approval/prd-approval nem possuem job de aprovação; o disparo manual autoriza a operação completa.
4. Publicar os workflows também na branch padrão main, pelo fluxo de PR do mantenedor.
5. Resolver a quota, recriar base/banco e concluir o Job database-init antes do deploy. Publicar os artefatos do commit antes de executar auth-provision.

Quota conferida em 2026-10-11: total/não reservada 10. Primeira tentativa de implantação em 2026-10-12 falhou com reserva cinco: a AWS exigiu mínimo de dez execuções NÃO reservadas e a stack terminou em ROLLBACK_COMPLETE. O mínimo informado não é uma reserva mínima por função.

Por decisão do mantenedor em 2026-10-12, testar ReservedConcurrentExecutions: 10 no template comum hom/prd antes de retirar a proteção. Esse ajuste aumenta a reserva da função, não a quota da conta, e pode reproduzir o bloqueio de capacidade não reservada. Não remover a reserva antecipadamente nem implementar fallback automático. Se a nova tentativa falhar por esse motivo, seguir a decisão de removê-la em uma correção posterior. Pedido de quota 110 continua pendente; esta alteração não solicita aumento de quota nem modifica o bootstrap.

Após publicar a alteração por PR: auth-destroy para excluir a stack ROLLBACK_COMPLETE → auth-publish na revisão com reserva dez → auth-provision com o NOVO artifact_commit publicado. Usar a revisão antiga ainda implantaria reserva cinco. O destroy remove o ZIP atual; templates/OpenAPI publicados continuam imutáveis por commit. Base/banco podem permanecer provisionados. Execuções AWS continuam manuais pelo mantenedor.

## CI

auth-ci roda em PRs develop/main e manual. Usa role CI restrita aos dois objetos Auth.Contracts 1.0.0/checksum, sem Environment de deploy ou leitura de Secrets. Mantém o hash conhecido b9080cea9d303722ac072e89421505d728126cdb52d57549da4328b1e18ab4e9, feed/mapeamento NuGet e cache separado pelo hash.

Executa restore/build Release e todos os testes .NET/PostgreSQL. Docker indisponível é erro; testes não são ignorados. O segundo job usa sam validate --lint e actionlint 1.7.12. OpenAPI continua estático, sem lint próprio. PR de fork deve ser revisado e trazido a uma branch do repositório para obter o pacote por OIDC; não usar pull_request_target.

## Publicar — auth-publish

Disparo manual com environment, somente develop/hom ou main/prd. Dois jobs, checkout do SHA da execução e fila auth-<ambiente> sem cancelamento:

- package usa role CI, verifica pacote e executa testes; sam build compila Function linux-x64/framework-dependent. Confere DLL, deps, runtimeconfig e Certificates/rds-ca.pem. O build e o OpenAPI passam ao segundo job pelo ID do artefato GitHub da mesma execução, retido sete dias, incluindo a pasta .aws-sam.
- publish usa role auth do Environment. sam package envia o ZIP nativamente a sam/auth/<ambiente>/<commit>/ e produz o template JSON. Não recompila nem constrói manifestos por scripts. CodeUri no template empacotado é uma string s3://<bucket>/<chave>; os workflows leem essa URI, exigem o bucket de artefatos e extraem a chave, conferindo ambiente/commit. --use-json define a serialização do template, sem converter CodeUri em objeto Bucket/Key.

O ZIP usa chave de conteúdo gerada pelo SAM, deduplicação e bucket versionado. Esse prefixo tem exceção localizada à exigência If-None-Match; não usar --force-upload. Portanto ele não oferece a garantia de gravação única dos contratos. O workflow registra commit, hash SHA-256 e versão S3 do ZIP obtidos sem executar a função.

Publicar separadamente:

| Objeto | Destino |
|---|---|
| Template empacotado | contracts/auth/<commit>/sam-<ambiente>.json |
| OpenAPI | contracts/auth/<commit>/openapi.json |
| Checksums | Os mesmos nomes com .sha256 |

Templates/OpenAPI/checksums mantêm SHA-256 do S3 e If-None-Match: *. Em PreconditionFailed, baixar e comparar bytes: aceitar somente conteúdo idêntico; outros erros ou divergência interrompem a publicação. Reexecução parcial completa somente objetos faltantes. O resumo registra hashes e versões. Não há aprovação Terraform na publicação.

## Provisionar — auth-provision

Inputs environment e artifact_commit, SHA completo minúsculo de 40 caracteres, publicado e ancestral da branch selecionada. artifact_commit seleciona código/template juntos; github.sha fixa o workflow/configuração de destino.

Um job deploy, sem aprovação intermediária. O disparo manual autoriza executar o fluxo completo:

1. Baixar template/checksum, conferir hash e referência do ZIP. Ler os quatro parâmetros públicos com AWS CLI: base/v2/vpc-id, workload-subnet-ids, auth-security-group-id e database/v2/security-group-id. jq converte o array JSON das subnets para a lista CSV do template. Conferir VPC/ambiente, duas subnets sem IP público automático em AZs distintas e SGs na mesma VPC. Não ler senhas ou JWT.
2. Executar sam deploy --no-confirm-changeset --no-fail-on-empty-changeset, com configuração do ambiente e parâmetros resolvidos. SAM prepara/executa as alterações, aguarda a conclusão e mostra eventos/outputs; sem comandos CloudFormation diretos, parsing de ARN ou divisão plan/apply.
3. Reter template/checksum/log nativo SAM por sete dias, inclusive em falha. Não recompilar a função ou publicar outro ZIP neste workflow. Alterações vazias encerram com sucesso.

A função publica versão/alias live com AutoPublishAliasAllProperties: true e AWS::LanguageExtensions antes de SAM. SSM function-version e invocation-arn apontam à versão numérica, não ao alias. ZipKey vem do template empacotado.

Rollback padrão CloudFormation permanece habilitado e é conduzido pelo SAM. Não usar sam sync, --disable-rollback ou rollback próprio. Falha inicial pode deixar ROLLBACK_COMPLETE; revisar o log SAM antes de descartar/recriar. Conferência funcional real será posterior ao deploy. O mantenedor coordena operações sequenciais, inclusive entre repositórios.

## Descartar — auth-destroy

Input somente environment; um job destroy e nenhuma aprovação intermediária. O disparo manual autoriza todo o descarte. Mesma fila auth-<ambiente>, branch e OIDC dos demais workflows.

SAM CLI 1.167.0 usa o mesmo samconfig.toml do deploy, sem comandos CloudFormation diretos:

```bash
sam delete \
  --config-env "$TARGET_ENVIRONMENT" \
  --config-file "$GITHUB_WORKSPACE/samconfig.toml" \
  --stack-name "$STACK_NAME" \
  --region "$AWS_REGION" \
  --no-prompts
```

A configuração hom/prd fornece o bucket compartilhado e o prefixo sam/auth/<ambiente>/changesets. O SAM remove o ZIP referenciado no template atual, limpa os templates operacionais desse prefixo e exclui/aguarda a stack. Não ampliar o prefixo para sam/auth/<ambiente>: ZIPs de outras revisões devem permanecer. Stack ausente segue o comportamento nativo de sucesso sem descarte; não exige commit/ZIP ou SSM da base. Erros do SAM interrompem o workflow.

| Removido | Preservado |
|---|---|
| Função/versões/alias, logs, egress e auth/v2 administrados pela stack | IAM runtime/CI/deploy persistente, SG da base, banco e outro ambiente |
| ZIP referenciado pelo deploy atual e templates operacionais SAM | Bucket, ZIPs de outras revisões, contracts/auth/ e pacote NuGet |

As roles auth têm DeleteObject exclusivamente em sam/auth/<ambiente>/* e ListBucket condicionado ao mesmo prefixo. Não têm DeleteObjectVersion, DeleteBucket ou exclusão em contracts/auth/, packages/, outro ambiente ou backend Terraform. SAM usa CloudFormation internamente: ListChangeSets permite inspecionar implantações parciais; DescribeStacks na stack auxiliar de nome calculado pelo SAM permite sua verificação, sem conceder exclusão de stack auxiliar/ECR.

O bucket permanece versionado: DeleteObject cria marcador de exclusão, preservando versões anteriores. Não introduzir limpeza de versões ou lifecycle. Antes de recriar o deploy removido, executar auth-publish na revisão desejada para disponibilizar o ZIP. A publicação continua aceitando apenas contratos/template com bytes idênticos; build divergente exige nova revisão. O SAM pode remover artefatos antes de concluir a exclusão da stack; falhas não restauram esses arquivos automaticamente. Conferir o log e republicar quando necessário antes de recriar o deploy.

Após o descarte, consultar SGs pelo nome mecanica-<ambiente>-auth via EC2 e listar as ENIs de cada SG encontrado, sem ler configuração da stack ou depender do SSM. Aguardar sua liberação antes de descartar banco/base; IAM runtime permanece para permitir a limpeza pela AWS. Ordem: Gateway/consumidores → auth/API → banco → base. Bootstrap fora do descarte normal.

Referências: [sam delete](https://docs.aws.amazon.com/serverless-application-model/latest/developerguide/sam-cli-command-reference-sam-delete.html) e [exclusão em S3 versionado](https://docs.aws.amazon.com/AmazonS3/latest/userguide/DeletingObjectVersions.html).

## Validação local e implantação posterior

Comandos padrão na raiz, com pacote já obtido:

~~~powershell
dotnet restore GerenciamentoMecanicaAutenticacao.slnx
dotnet build GerenciamentoMecanicaAutenticacao.slnx -c Release --no-restore
dotnet test GerenciamentoMecanicaAutenticacao.slnx -c Release --no-build --no-restore
sam validate --lint --template-file template.yaml --region us-east-1
sam build --template-file template.yaml
actionlint
~~~

Em 2026-10-11: 217 testes aprovados, zero ignorados; build Release e SAM aprovados; certificado/dependências/runtimeconfig conferidos. O empacotamento local nativo SAM produziu bytes idênticos em duas execuções sobre o mesmo build. Isso não comprova uploads/reexecução de publicação na AWS, TLS RDS ou deploy SAM real.

Em 2026-10-12: corrigida a leitura de CodeUri nos workflows publish/provision para a URI S3 string produzida pelo SAM. actionlint/ShellCheck aprovados; execução dos trechos reais com Bash/jq em arquivos temporários reproduziu o erro anterior e aprovou 16 cenários: hom/prd válidos e rejeição de bucket, ambiente, commit ou tipo incorreto. Nenhum script de teste foi acrescentado ao repositório e nenhuma operação AWS foi executada nessa correção.

Sequência: bootstrap aplicado → configuração GitHub → quota suficiente → base → banco/Job → publicação → deploy SAM manual → hom. Depois conferir versão numérica, hash do código, concorrência, SSM, logs e invocações inválida/inexistente/elegível sem imprimir documentos/JWT. Repetir o provisionamento sem alterações deve encerrar com sucesso pelo SAM. Validar destroy somente mediante pedido de descarte.

E3.1/E3.2/E3.6/E3.9 e E2.12 permanecem abertas para suas comprovações reais. Gateway, compatibilidade real com API e New Relic/OTel continuam entregas posteriores.
