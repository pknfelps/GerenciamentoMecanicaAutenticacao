resource "aws_cloudwatch_log_group" "auth" {
  name              = "/aws/lambda/${local.name_prefix}"
  retention_in_days = 7
}

resource "aws_lambda_function" "auth" {
  function_name                  = local.name_prefix
  description                    = "Validacao de cliente e emissao JWT - ${var.artifact_commit}"
  role                           = data.aws_iam_role.runtime.arn
  package_type                   = "Zip"
  runtime                        = "dotnet10"
  architectures                  = ["x86_64"]
  handler                        = "GerenciamentoMecanica.Autenticacao.Function::GerenciamentoMecanica.Autenticacao.Function.FunctionHandler::HandleAsync"
  memory_size                    = 512
  timeout                        = 15
  reserved_concurrent_executions = 5
  publish                        = true
  s3_bucket                      = local.artifacts_bucket
  s3_key                         = data.aws_s3_object.zip.key
  s3_object_version              = data.aws_s3_object.zip.version_id
  code_sha256                    = var.zip_sha256_base64

  environment {
    variables = { Runtime__Environment = var.environment }
  }

  vpc_config {
    subnet_ids         = local.subnet_ids
    security_group_ids = [data.aws_security_group.auth.id]
  }

  logging_config {
    log_format            = "JSON"
    log_group             = aws_cloudwatch_log_group.auth.name
    application_log_level = "INFO"
    system_log_level      = "INFO"
  }

  depends_on = [
    aws_vpc_security_group_egress_rule.postgres,
    aws_vpc_security_group_egress_rule.https
  ]

  lifecycle {
    postcondition {
      condition     = can(regex("^[1-9][0-9]*$", self.version))
      error_message = "A funcao deve publicar uma versao numerica; consumidores nao usam $LATEST."
    }
    precondition {
      condition     = terraform.workspace == "default"
      error_message = "Usar workspace default e backend separado por ambiente."
    }
    precondition {
      condition     = alltrue([for parameter in data.aws_ssm_parameter.network : parameter.type == "String"])
      error_message = "Os identificadores de rede devem ser parametros String publicos."
    }
    precondition {
      condition     = length(local.subnet_ids) == 2 && alltrue([for subnet in data.aws_subnet.workload : subnet.vpc_id == data.aws_vpc.environment.id && !subnet.map_public_ip_on_launch]) && length(toset([for subnet in data.aws_subnet.workload : subnet.availability_zone])) == 2
      error_message = "Exigir duas subnets privadas de workloads, em zonas distintas da VPC do ambiente."
    }
    precondition {
      condition     = data.aws_security_group.auth.vpc_id == data.aws_vpc.environment.id && data.aws_security_group.database.vpc_id == data.aws_vpc.environment.id && data.aws_security_group.auth.name == local.name_prefix && lookup(data.aws_vpc.environment.tags, "Environment", "") == var.environment
      error_message = "VPC e SGs devem corresponder ao ambiente; usar o SG auth reservado pela base."
    }
    precondition {
      condition     = try(length(data.aws_s3_object.zip.version_id) > 0 && data.aws_s3_object.zip.version_id != "null", false)
      error_message = "Publicar o ZIP no bucket com versionamento habilitado antes do plan."
    }
    precondition {
      condition     = data.aws_s3_object.zip.content_length > 0 && data.aws_s3_object.openapi.content_length > 0
      error_message = "ZIP e OpenAPI devem estar publicados e nao vazios para o mesmo commit."
    }
  }
}