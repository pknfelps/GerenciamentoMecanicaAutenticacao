variable "environment" {
  description = "Ambiente de autenticacao, com estado separado e workspace default."
  type        = string
  validation {
    condition     = contains(["hom", "prd"], var.environment)
    error_message = "environment deve ser hom ou prd."
  }
}

variable "aws_region" {
  type    = string
  default = "us-east-1"
  validation {
    condition     = var.aws_region == "us-east-1"
    error_message = "Esta unidade atende somente us-east-1."
  }
}

variable "artifact_commit" {
  description = "SHA completo do commit que publicou ZIP e contrato no bucket compartilhado."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-f]{40}$", var.artifact_commit))
    error_message = "artifact_commit deve ser um SHA Git completo de 40 caracteres hexadecimais minusculos."
  }
}

variable "zip_sha256_base64" {
  description = "SHA-256 do ZIP publicado, em Base64; nao usar ETag S3."
  type        = string
  validation {
    condition     = can(regex("^[A-Za-z0-9+/]{43}=$", var.zip_sha256_base64))
    error_message = "zip_sha256_base64 deve representar um SHA-256 em Base64 (44 caracteres)."
  }
}