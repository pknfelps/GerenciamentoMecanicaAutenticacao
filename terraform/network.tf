# A base possui o SG. Este estado possui somente suas duas regras de saida.
resource "aws_vpc_security_group_egress_rule" "postgres" {
  security_group_id            = data.aws_security_group.auth.id
  referenced_security_group_id = data.aws_security_group.database.id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  description                  = "PostgreSQL privado do ambiente"
  tags                         = { Name = "${local.name_prefix}-postgres-egress" }
}

resource "aws_vpc_security_group_egress_rule" "https" {
  security_group_id = data.aws_security_group.auth.id
  cidr_ipv4         = "0.0.0.0/0"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  description       = "SSM e Secrets Manager via NAT existente"
  tags              = { Name = "${local.name_prefix}-https-egress" }
}