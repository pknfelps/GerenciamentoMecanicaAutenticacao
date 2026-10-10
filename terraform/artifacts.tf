data "aws_s3_object" "zip" {
  bucket = local.artifacts_bucket
  key    = local.zip_key
}

data "aws_s3_object" "openapi" {
  bucket = local.artifacts_bucket
  key    = local.openapi_key
}