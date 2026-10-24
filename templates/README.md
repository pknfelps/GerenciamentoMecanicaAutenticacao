# Templates SAM por função

- `auth.yaml`: CodeUri relativo `../AuthFunction/`. Preserva logical IDs, nomes físicos, handler, recursos e catálogo auth/v2. Deploy atual usa `samconfig.auth.toml`.
- `email.yaml`: CodeUri `../EmailFunction/`, função privada dotnet10/x86_64, 512 MB/6 s, alias `live`, versão numérica, logs de sete dias e catálogo email/v1. Usa role e SG já preparados no bootstrap/base; administra somente egress SMTP para o SG do NLB. Não possui eventos públicos, URL, layer, banco/JWT ou dependências da autenticação.

Dois templates/stacks independentes. Não reutilizar a stack auth para envio. O catálogo email/v1 publica function-name, function-version, invocation-arn do alias, contract-version=1 e zip-key. O deploy resolve subnets, SGs e endpoint SMTP do catálogo base/v2 e informar o commit/ZIP imutável. Publicação/provisionamento usam os [workflows por componente](../.github/workflows).

Usar diretórios de build separados para não substituir a saída de outra função:

```powershell
sam validate --lint --template-file templates/email.yaml --region us-east-1
sam build --template-file templates/email.yaml --build-dir artifacts/ae5/sam-email
sam validate --lint --template-file templates/auth.yaml --region us-east-1
sam build --template-file templates/auth.yaml --build-dir artifacts/ae5/sam-auth
```

SAM gera o template de saída em cada diretório. [Execução local](../README.md#emailfunction-local). Validação/build não comprovam IAM, rede ou execução na AWS.
