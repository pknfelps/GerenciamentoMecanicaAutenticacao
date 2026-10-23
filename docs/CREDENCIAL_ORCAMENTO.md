# Credencial de orçamento — contrato LA.1

> **REGISTRO HISTÓRICO — desenho substituído em 22/10/2026.** A decisão vigente é autenticação por código solicitado pelo cliente, captura SMTP4dev e JWT Customer sem OS, com duas funções/stacks e repositório futuro GerenciamentoMecanicaServerless. [Plano AE.1–AE.10](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTENTICACAO_E_EMAIL.md). Os resultados abaixo referem-se à implementação LA local; não continuar sua ativação nem considerar este contrato o alvo da próxima entrega. Código e recursos não foram alterados por esta anotação.

**22/10/2026 — validação implementada e comprovada localmente na autenticação.** A emissão na API, a validação do escopo no backend e a ativação Gateway/SAM pertencem às próximas etapas do plano extra. Não publicar isoladamente a mudança de autenticação: o fluxo anterior por documento deixa de autenticar.

## Credencial pública apresentada pelo cliente

| Campo | Regra |
|---|---|
| Assinatura / `alg` | HS256, com a chave JWT já carregada do ambiente; token assinado obrigatório |
| `typ` do header JWT | `mecanica-budget+jwt` |
| `iss` | Issuer já configurado para o ambiente |
| `aud` | Audience configurada acrescida exatamente de `/budget-decision`; uma única audiência, sem equivalência de barra final |
| `sub` | UUID não vazio do cliente, formato D |
| `order_id` | UUID não vazio da OS, formato D |
| `purpose` | `budget-decision` |
| `exp` | Obrigatório, instante UTC em segundos Unix; vencimento rejeitado sem tolerância adicional de relógio |
| `nbf` | Opcional; quando presente, não pode estar no futuro nem após a expiração |

Os claims de identidade/finalidade devem ser únicos. Documento, nome e role não devem integrar a credencial pública. A Lambda não utiliza valores recebidos como cliente/OS fora da credencial validada; na aprovação, compara também order_id com a rota antes de consultar o cliente. Limite de entrada: 8.192 caracteres ASCII do token. Tokens genéricos, internos, não assinados ou malformados não substituem esta credencial.

O prazo de emissão da credencial pública será definido junto ao emissor na LA.3; a Lambda respeita exp e não inventa uma validade própria nem consulta o estado da OS. O estado, vínculo e bloqueio transacional são responsabilidade da API.

## Entrada e consulta do cliente

- Authorizer REQUEST: Authorization Bearer com a credencial pública, X-Customer-Document com CPF/CNPJ e UUID da OS na rota PATCH /Orders/{id}/budget. Body approved não é recebido pela Lambda.
- POST /customers/validate: o mesmo Authorization Bearer e body JSON somente com document. Documento não é duplicado em header/body nesse endpoint; o body continua obrigatório e estrito.
- Headers de credenciais ignoram maiúsculas no nome, mas múltiplos valores, chaves repetidas ou representações conflitantes são recusados. Mirror único idêntico é aceito.
- Após validação criptográfica/escopo, consultar somente id/name/document/status de customers, por parâmetro UUID. Documento normalizado deve corresponder ao cadastro Active. Token do cliente A com documento do cliente B é negado, mesmo com ambos ativos.
- Nenhuma leitura de orders ou novo grant é necessária. Falha SQL/bootstrap/timeout continua técnica; não se converte em negativa de elegibilidade.

## JWT interno para a API

Preserva issuer/audience e HS256 atuais, nome/UUID do cadastro e role Customer. Acrescenta order_id e purpose=budget-decision. Expiração é o menor valor entre agora + dez minutos e exp da credencial pública; não há renovação além do token apresentado.

O authorizer envia esse JWT somente no contexto escalar customerAuthorization com Bearer; policy Allow permanece limitada ao methodArn solicitado e principalId é o UUID do cliente. O Gateway deverá substituir Authorization na integração. A rota HTTP retorna o JWT interno como string JSON, com Cache-Control: no-store, preservando 200/400/401/500 e mensagens fixas.

**A API atual ainda precisa implementar a verificação do escopo order_id/finalidade (LA.3).** Carregar claims no token não comprova que o backend os aplica. A credencial pública usa audiência distinta e não é JWT de acesso genérico da API. Não há tabela de tokens, revogação individual, garantia de uso único ou entrega real ao cliente nesta etapa.

## Validação local e publicação

Testes descritivos existentes foram ampliados com assinatura, audiência, tipo/finalidade, expiração, claims obrigatórios/ambíguos, UUID, token/documento incompatíveis, rota, reuso/concorrência/cancelamento e PostgreSQL real. A role SQL só lê as colunas já autorizadas de customers; a autenticação foi exercitada sem acesso a orders.

Nenhum token/documento deve aparecer em logs, traces, exemplos versionados ou evidências de execução. As credenciais dos testes são sintéticas. A conferência do runtime gerenciado, mapeamento Gateway e contratos completos ocorrerá depois de implementar as etapas pendentes e mediante autorização de publicação.
