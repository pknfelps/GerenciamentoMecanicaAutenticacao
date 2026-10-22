# AE.5 — EmailFunction e captura local

Preparada e validada em 22/10/2026. Etapa extra, separada dos checklists oficiais concluídos. Nenhum login, plano/apply, deploy ou workflow remoto foi executado.

## Implementação

`EmailFunction/FunctionHandler.cs` recebe Stream e aplica o contrato estrito antes de inicializar o transporte. `Contracts/EmailRequestParser.cs` limita leitura a 128 KiB mais um byte de detecção, rejeita JSON/UTF-8 inválido, campos desconhecidos/duplicados, tipos incorretos e controles de cabeçalho. São aceitos customer_access_code, budget_available e status_changed. toName pode ser vazio; toEmail é um mailbox único sem nome embutido; subject/textBody são obrigatórios; htmlBody é opcional ou null. Limites de corpos são em bytes UTF-8, conforme o contrato central.

`Configuration/SmtpSettings.cs` valida a configuração uma vez, na primeira mensagem válida. `Smtp/SmtpEmailSender.cs` usa MailKit 4.18.1, remetente fixo e MIME texto/HTML, com uma conexão por chamada. Reutiliza somente a configuração/transporte sem estado. Sem SQL/JWT, AWS SDK, autenticação SMTP, retry, fila ou persistência. System.Security.Cryptography.Pkcs 10.0.13 está fixado para evitar a versão transitiva vulnerável, sem supressões.

O orçamento de até três segundos começa antes do parsing e inclui configuração, MIME e SMTP. É reduzido pelo RemainingTime, reservando um segundo para resposta. O socket é descartado após a aceitação do DATA, sem esperar outro comando SMTP. Timeout não garante que a mensagem deixou de ser capturada: repetir pode duplicar.

Resposta de sucesso: `{"version":1,"outcome":"captured"}`. Falha: `{"version":1,"outcome":"failure","error":"invalid-request|smtp-unavailable|timeout|technical-failure"}` (um único código). Captured significa aceitação SMTP, sem entrega externa ou garantia de retenção. A visualização HTTP da caixa pode aparecer depois do ACK, pois seu processamento é assíncrono.

Uma conclusão JSON técnica registra mensagem fixa, lambdaRequestId, tipo permitido, outcome, código fixo do erro, duração e nome do tipo da exceção. Sem destinatário, conteúdo, código, configuração, mensagem original, stack ou inner exception. O formato JSON nativo da Lambda envolve esse JSON no seu campo `message`, sem criar outro log de conclusão da aplicação.

`templates/email.yaml` prepara a função privada, role persistente existente, VPC/SG, egress somente NLB:SMTP, alias live, versão numérica, logs de sete dias e cinco metadados email/v1. Não requer saída HTTPS do código, pois não usa AWS SDK ou extensão. Não cria endpoint público, instrumentação adicional ou recursos da autenticação. Publish/provision/destroy e proteções específicas serão integrados na AE.8.

## Execução local direta

Docker Linux e SDK de `global.json` devem estar disponíveis. Não iniciar Docker Desktop automaticamente. Na raiz Serverless, restore/build/testes usam a solução existente e as fixtures PostgreSQL/SMTP descartáveis:

```powershell
dotnet restore GerenciamentoMecanicaServerless.slnx
dotnet build GerenciamentoMecanicaServerless.slnx -c Release --no-restore
dotnet test GerenciamentoMecanicaServerless.slnx -c Release --no-build --no-restore
sam validate --lint --template-file templates/email.yaml --region us-east-1
sam build --template-file templates/email.yaml --build-dir artifacts/ae5/sam-email
```

Para o SAM, abrir um terminal dedicado com credenciais fictícias e arquivos vazios locais. Essas variáveis valem somente para esse processo; não persistir na configuração AWS do usuário. O endpoint inacessível impede chamadas AWS reais da ferramenta, sem impedir a API Invoke do emulador:

```powershell
New-Item -ItemType Directory -Force artifacts/ae5 | Out-Null
Set-Content artifacts/ae5/empty-aws-config ''
$env:AWS_CONFIG_FILE = (Resolve-Path artifacts/ae5/empty-aws-config).Path
$env:AWS_SHARED_CREDENTIALS_FILE = $env:AWS_CONFIG_FILE
$env:AWS_ACCESS_KEY_ID = 'local'
$env:AWS_SECRET_ACCESS_KEY = 'local'
Remove-Item Env:AWS_SESSION_TOKEN, Env:AWS_PROFILE -ErrorAction SilentlyContinue
$env:AWS_EC2_METADATA_DISABLED = 'true'
$env:AWS_ENDPOINT_URL = 'http://127.0.0.1:9'
$env:AWS_REGION = 'us-east-1'
$env:AWS_DEFAULT_REGION = 'us-east-1'
$env:SAM_CLI_TELEMETRY = '0'

# Valores fictícios apenas para interpolar o template; não cria banco ou API.
$env:POSTGRES_PASSWORD = 'LocalOnlyUnusedPassword'
$env:JWT_KEY = 'local-only-unused-jwt-key-32-characters'
docker compose -f ../GerenciamentoMecanicaSistema/docker-compose.yaml -p mecanica-email-local up -d --no-deps --wait smtp

$parameters = @(
  'Environment=hom',
  'ArtifactCommit=0000000000000000000000000000000000000000',
  'WorkloadSubnetIds=subnet-00000000000000001,subnet-00000000000000002',
  'EmailSecurityGroupId=sg-00000000000000001',
  'NlbSecurityGroupId=sg-00000000000000002',
  'SmtpHost=smtp', 'SmtpPort=25', 'ZipKey=sam/email/hom/local.zip'
)
sam local start-lambda --template-file artifacts/ae5/sam-email/template.yaml --host 127.0.0.1 --port 3001 --docker-network mecanica-email-local_default --warm-containers LAZY --parameter-overrides $parameters
```

O runtime local recebe hom/us-east-1 apenas para validar a mesma configuração; o transporte não lê AWS. SAM pode baixar/construir a imagem oficial dotnet10, mas não exige layer, conta ou serviços AWS para o envio. Em outro terminal, enviar uma mensagem fictícia:

```powershell
$message = @{
  version = 1; type = 'customer_access_code'; toName = 'Cliente de teste'
  toEmail = 'customer@example.invalid'; subject = 'Codigo de acesso'
  textBody = 'Codigo ficticio: 01234567'
  htmlBody = '<p>Codigo ficticio: <strong>01234567</strong></p>'
} | ConvertTo-Json -Compress
$response = Invoke-WebRequest -Method Post -Uri http://127.0.0.1:3001/2015-03-31/functions/EmailFunction/invocations -ContentType application/json -Body ([Text.Encoding]::UTF8.GetBytes($message))
$response.Content
```

Conferir HTTP 200, ausência de FunctionError e resposta v1 captured; abrir `http://127.0.0.1:3000` para ver a captura. Nenhum e-mail externo é enviado. Mensagens contêm o código necessário ao teste; não publicar a caixa ou salvá-la como evidência com dados reais.

Encerrar SAM com Ctrl+C; depois executar, no terminal com as variáveis fictícias:

```powershell
docker compose -f ../GerenciamentoMecanicaSistema/docker-compose.yaml -p mecanica-email-local down
```

Não usar `--volumes`: o PostgreSQL mantém seu volume. A caixa SMTP é em memória e limitada a 100 mensagens/sessões, com relay desabilitado e configuração bloqueada. Um volume smtp_data de execução antiga deixa de ser montado, mas não é apagado automaticamente. Reiniciar SMTP perde a caixa; solicitar outro código quando o fluxo AE.6 estiver disponível.

## Evidências locais

| Conferência | Resultado |
|---|---|
| Restore/build Release da solução | Aprovados, zero avisos/erros |
| Regressão completa | 419 aprovados, zero falhas/ignorados; 91 novos da EmailFunction (84 unitários e 7 SMTP) |
| Testes SMTP4dev 3.15.0/Testcontainers | Três tipos, MIME texto/HTML/remetente fixo, texto simples, concorrência, recusa SMTP e timeout/fechamento do socket |
| Testes de contrato/handler | Limites UTF-8/envelope, erros, uma conclusão sanitizada, reutilização, reserva/deadline e payload inválido sem criar transporte |
| SAM validate --lint/build email e auth | Aprovados com SAM 1.167.0 e SDK 10.0.402 |
| Publish/ZIP linux-x64 | Framework-dependent/net10.0; email sem Npgsql/JWT/auth/testes; auth com certificado RDS original e configurações OTel |
| Primeira invocação SAM isolada | Captured; mensagem visível, sem relay; não dependeu de segunda chamada |
| Reutilização/entrada inválida | Nova captura e invalid-request, sem mensagem extra para entrada inválida |
| SMTP interrompido | Timeout tratado em aproximadamente três segundos; sem FunctionError ou exceção original |
| SAM e SMTP real do Compose da API | Budget capturado; montagem sem volume SMTP; caixa vazia após restart |
| Logs de aplicação/runtime/SMTP | Sem conteúdo fictício sensível, destinatário, código ou stack; somente campos técnicos permitidos na conclusão |
| Auth/OpenAPI/workflows | Fontes e contratos existentes preservados |

Relatório local ignorado pelo Git: `TestResults/ae5-full-regression.trx`. Respostas/logs sanitizados: `artifacts/ae5/`; ZIPs inspecionados: `email-linux-x64.zip` e `auth-linux-x64.zip`. SAM deixa os diretórios de publish extraídos; os ZIPs foram montados desses diretórios para conferir conteúdo. Não publicar os artefatos locais como se fossem releases imutáveis da pipeline.

Na máquina desta validação, o SDK temporário 10.0.402 compilou os projetos; sua cópia incompleta do runtime foi contornada executando os testes compilados com VSTest/runtime 10.0.9 instalados. O SAM também executou o pacote no runtime oficial dotnet10 em container. Nenhuma alteração de produto ou teste ignorado foi usada para contornar o ambiente.

## Próximas etapas e limites

AE.6 conecta AuthFunction ao store transacional e ao Invoke v1 e implementa request-code/verify-code. AE.7 troca os adaptadores SMTP/logging da API pelo Invoke, inclusive no caminho container → SAM, e trata Active/decisão de orçamento. A API atual ainda mantém seu transporte anterior; a centralização estará completa ao conectar os dois consumidores.

AE.8 integra os templates/catálogos/artefatos aos workflows e atualiza o Gateway; AE.9 comprova os fluxos locais completos; AE.10 depende de autorização para implantação. Alias, IAM, SG/NLB/VPC, cold start, congelamento, capacidade e duração reais AWS não foram certificados pelo emulador. Nenhum recurso temporário de validação permanece em execução. Não há garantia de entrega única, recuperação de caixa ou entrega ao destinatário.
