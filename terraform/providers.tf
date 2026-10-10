provider "aws" {
  region              = var.aws_region
  allowed_account_ids = ["121754142617"]
  default_tags {
    tags = {
      Project     = "mecanica"
      Environment = var.environment
      Component   = "auth"
      ManagedBy   = "Terraform"
    }
  }
}