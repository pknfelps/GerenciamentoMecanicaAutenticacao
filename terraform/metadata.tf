locals {
  configuration = {
    function-name    = aws_lambda_function.auth.function_name
    function-version = aws_lambda_function.auth.version
    # ARN Lambda qualificado, nao a URI de integracao REST do Gateway.
    invocation-arn         = aws_lambda_function.auth.qualified_arn
    zip-key                = local.zip_key
    openapi-key            = data.aws_s3_object.openapi.key
    auth-contracts-version = "1.0.0"
  }
}

resource "aws_ssm_parameter" "configuration" {
  for_each = local.configuration
  name     = "/mecanica/${var.environment}/auth/v2/${each.key}"
  type     = "String"
  tier     = "Standard"
  value    = each.value
}

output "configuration" {
  description = "Configuracao publica; parametros nao comprovam prontidao operacional."
  value       = local.configuration
}