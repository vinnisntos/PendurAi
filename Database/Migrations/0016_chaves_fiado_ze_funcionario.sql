-- =============================================================================
-- Migration 0016. Idempotente. Precisa estar aplicada ANTES do deploy do código
-- que a usa (branch refactor/visual-e-logica).
-- =============================================================================

-- 1) Chaves do ASP.NET Data Protection (cookie de login e antiforgery) no banco:
--    no disco da VPS elas podiam se perder a cada deploy e deslogar todos os caixas.
CREATE TABLE IF NOT EXISTS data_protection_keys (
    nome       text PRIMARY KEY,
    xml        text NOT NULL,
    criado_em  timestamptz NOT NULL DEFAULT now()
);

-- 2) Histórico de pagamentos do fiado (antes só o saldo mudava, sem rastro).
CREATE TABLE IF NOT EXISTS clientes_pagamentos (
    id               uuid PRIMARY KEY,
    tenant_id        uuid NOT NULL REFERENCES tenants(id),
    cliente_id       uuid NOT NULL REFERENCES clientes(id),
    valor            numeric(12,2) NOT NULL CHECK (valor > 0),
    forma_pagamento  text NOT NULL,
    usuario_id       uuid,
    turno_id         uuid,
    criado_em        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_clientes_pagamentos_cliente ON clientes_pagamentos (tenant_id, cliente_id, criado_em DESC);

-- 3) Pedido externo (Zé Delivery) vira no máximo UMA venda: reentrega ou evento de
--    mudança de status do mesmo pedido não duplica venda nem baixa estoque duas vezes.
ALTER TABLE vendas ADD COLUMN IF NOT EXISTS referencia_externa text;
CREATE UNIQUE INDEX IF NOT EXISTS ux_vendas_referencia_externa
    ON vendas (tenant_id, tipo_origem, referencia_externa)
    WHERE referencia_externa IS NOT NULL;

-- 4) Funcionário desligado perde o acesso sem apagar o histórico de vendas dele.
ALTER TABLE profiles ADD COLUMN IF NOT EXISTS ativo boolean NOT NULL DEFAULT true;

-- Tabelas novas fora da API REST pública (o app acessa só pela conexão direta).
ALTER TABLE data_protection_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE clientes_pagamentos  ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON data_protection_keys FROM anon, authenticated;
REVOKE ALL ON clientes_pagamentos  FROM anon, authenticated;
