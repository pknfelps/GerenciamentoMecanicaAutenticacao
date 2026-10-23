# AE.2 — Organização e migração do repositório

**22/10/2026 — AE.2 concluída.** Implementação local, renomeação GitHub/raiz/remote, proteções, OIDC e retirada da confiança antiga conferidos. [Plano extra AE](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/PLANO_EXTRA_AUTENTICACAO_E_EMAIL.md).

## Estrutura local

- Solução GerenciamentoMecanicaServerless.slnx, com AuthFunction/AuthFunctionTests no lugar de Function/FunctionTests.
- Assembly, namespace e handler da autenticação conservados, assim como HTTP/JWT, SQL, runtime reutilizado e instrumentação. Os 17 fontes C# movidos conservam o conteúdo.
- EmailFunction/EmailFunctionTests independentes e somente estruturais. Não há handler, SMTP, testes funcionais ou stack email. AE.5/AE.8 implementarão transporte/publicação; não publicar função vazia.
- templates/auth.yaml e samconfig.auth.toml; somente CodeUri mudou no template, para ../AuthFunction/. Logical IDs, nomes físicos e propriedades de execução preservados.
- Quatro workflows auth ajustados somente nos caminhos. Jobs configuration/dotnet preservados; saída SAM continua .aws-sam/build/template.yaml. Reutilização de workflows email continua pendente.

## GitHub, checkout e proteções

Repositório público pknfelps/GerenciamentoMecanicaServerless, mesmo ID 1375105446 e owner ID 114608389. Pasta C:/Users/felip/source/repos/GerenciamentoMecanicaServerless e origin https://github.com/pknfelps/GerenciamentoMecanicaServerless.git. HEAD e alterações locais conservados.

Ruleset 23791642 ativo para main/develop, sem bypass: PR e checks configuration/dotnet exigidos; exclusão e force push bloqueados. Environments hom/prd e os de aprovação existentes preservados; branches e variáveis de roles conferidas. Não foram alteradas proteções ou credenciais. Links operacionais atualizados; referências históricas datadas preservadas.

## Transição OIDC executada

O plano inicial aprovado adicionou o novo nome às três roles auth-ci/hom-auth/prd-auth, mantendo o antigo temporariamente. Aplicado exatamente o binário SHA-256 a0bb486730d9d35ad08b74691d11bf49f86205b3b6c63d87f91de1bc67a5fbd9: 0 criações, 3 alterações e 0 exclusões. Trust policies conferidas; permissões/runtime intactos.

[Auth-ci #16](https://github.com/pknfelps/GerenciamentoMecanicaServerless/actions/runs/39812817320) autorizado e executado uma vez em develop: sucesso em configuration/dotnet e autenticação OIDC confirmada. Commit remoto 01225acd7c45fe03a48ee7e6b228c069b77b4bdd, anterior à estrutura local ainda não publicada. Esse run comprova a identidade do novo nome; os testes locais comprovam a estrutura reorganizada.

O prefixo canônico local agora é repo:pknfelps@114608389/GerenciamentoMecanicaServerless@1375105446. Variável/exemplo temporários retirados após a transição. O [plano final](https://github.com/pknfelps/GerenciamentoMecanicaSistema/blob/develop/docs/AE2_FINALIZACAO_OIDC_2026-10-22.md) remove somente os subjects antigos das mesmas três roles: 0/3/0, SHA-256 5e7e5582a71ce98873669dbe7b72228b04fc9f14cea225baadadb05b55689f49. Aprovado e aplicado, com hash conferido: 0 criações, 3 alterações e 0 exclusões. Subjects antigos ausentes nas três roles e plano posterior sem diferenças (código 0). Não utilizar as instruções temporárias do plano inicial para gerar planos novos.

## Validação local

Restore/build Release sem avisos/erros; 269 testes existentes aprovados, zero falhas/ignorados, incluindo PostgreSQL/Testcontainers. Repetidos após a renomeação da pasta raiz, junto de SAM validate/build. EmailFunctionTests ainda não contém casos e não comprova envio.

Publish auth linux-x64 e build SAM conferidos: certificado RDS, JSON/YAML e runtimeconfig presentes; sem exporters InMemory/assemblies de testes. Publish estrutural email sem SQL/JWT/dependências auth. OpenAPI preservado, SHA-256 31193f934fcba6a66ab64129d7e039188f22f3c936a888ef5c43ce6abebe3995. Actionlint/ShellCheck e Terraform fmt/init sem backend/validate aprovados.

Evidências locais em artifacts/ae2 e artifacts/ae2/post-rename, fora do Git. Nenhum publish/provision/destroy remoto realizado. As alterações de estrutura continuam locais, para PR do mantenedor.

## Encerramento

Aplicado exclusivamente o plano final salvo aprovado. Conferência direta das três trust policies confirmou o novo nome exclusivo, mantendo principal, audience, branches e Environments. Terraform posterior retornou No changes, código 0. AE.2 concluída; evidências do apply e comparação em artifacts/ae2 da API, referenciadas no registro central de finalização.

Alterações locais seguem para PR do mantenedor. Nenhum commit/push, outro workflow, publish/provision/destroy ou ampliação de permissões email foi realizado nesta finalização. AE.3 não foi iniciada.
