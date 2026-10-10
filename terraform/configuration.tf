locals {
  name_prefix      = "mecanica-${var.environment}-auth"
  artifacts_bucket = "mecanica-artifacts-121754142617-us-east-1"
  zip_key          = "lambda/${var.artifact_commit}/function.zip"
  openapi_key      = "contracts/auth/${var.artifact_commit}/openapi.json"
  network_parameters = {
    vpc     = "/mecanica/${var.environment}/base/v2/vpc-id"
    subnets = "/mecanica/${var.environment}/base/v2/workload-subnet-ids"
    auth_sg = "/mecanica/${var.environment}/base/v2/auth-security-group-id"
    db_sg   = "/mecanica/${var.environment}/database/v2/security-group-id"
  }
  # Somente identificadores publicos String; nenhum valor de Secret e consultado.
  subnet_ids = toset(jsondecode(nonsensitive(data.aws_ssm_parameter.network["subnets"].value)))
}

data "aws_ssm_parameter" "network" {
  for_each        = local.network_parameters
  name            = each.value
  with_decryption = false
  lifecycle {
    precondition {
      condition     = terraform.workspace == "default"
      error_message = "Usar workspace default e backend separado por ambiente."
    }
    postcondition {
      condition     = self.type == "String" && length(trimspace(nonsensitive(self.value))) > 0
      error_message = "Os identificadores de rede devem ser parametros String publicos preenchidos."
    }
  }
}

data "aws_vpc" "environment" {
  id = nonsensitive(data.aws_ssm_parameter.network["vpc"].value)
  lifecycle {
    postcondition {
      condition     = lookup(self.tags, "Environment", "") == var.environment && lookup(self.tags, "Project", "") == "mecanica"
      error_message = "A VPC publicada deve pertencer ao projeto e ambiente esperados."
    }
  }
}

data "aws_subnet" "workload" {
  for_each = local.subnet_ids
  id       = each.value
  lifecycle {
    precondition {
      condition     = length(local.subnet_ids) == 2
      error_message = "Exigir duas subnets distintas de workloads."
    }
    postcondition {
      condition     = self.vpc_id == data.aws_vpc.environment.id && !self.map_public_ip_on_launch
      error_message = "As subnets de workloads devem ser privadas e pertencer a VPC do ambiente."
    }
  }
}

data "aws_security_group" "auth" {
  id = nonsensitive(data.aws_ssm_parameter.network["auth_sg"].value)
  lifecycle {
    postcondition {
      condition     = self.vpc_id == data.aws_vpc.environment.id && self.name == local.name_prefix
      error_message = "Usar exclusivamente o SG auth reservado na VPC do ambiente."
    }
  }
}

data "aws_security_group" "database" {
  id = nonsensitive(data.aws_ssm_parameter.network["db_sg"].value)
  lifecycle {
    postcondition {
      condition     = self.vpc_id == data.aws_vpc.environment.id && self.name == "mecanica-${var.environment}-postgres"
      error_message = "Usar exclusivamente o SG do banco na VPC do ambiente."
    }
  }
}

data "aws_iam_role" "runtime" {
  name = "${local.name_prefix}-runtime"
}