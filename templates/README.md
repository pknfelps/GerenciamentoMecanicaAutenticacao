# Templates SAM por função

- auth.yaml: template existente movido, com CodeUri relativo ../AuthFunction/. Preservar logical IDs, nomes físicos, handler, recursos e catálogos auth/v2. Configuração de deploy na raiz: samconfig.auth.toml.
- email.yaml: será criado quando o handler/transporte existir em AE.5 e integrado à publicação na AE.8. Não publicar uma função sem implementação nem reutilizar a stack auth para envio.

SAM gera .aws-sam/build/template.yaml durante o build; esse caminho de saída não foi renomeado. Wrappers atuais auth continuam funcionais com os novos caminhos. Reutilizáveis e wrappers email serão implementados na AE.8.
