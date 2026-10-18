-- =============================================================================
-- Migration 0018: canal WhatsApp (bot de atendimento) + pedidos online pagos via Pix.
-- Idempotente. Aplicar ANTES do deploy do código que a usa (branch feat/canal-whatsapp).
-- =============================================================================

-- 1) Cardápio: foto, descrição e quais produtos aparecem pro bot/cardápio público.
ALTER TABLE produtos ADD COLUMN IF NOT EXISTS descricao       text;
ALTER TABLE produtos ADD COLUMN IF NOT EXISTS foto_url        text;
ALTER TABLE produtos ADD COLUMN IF NOT EXISTS exibir_cardapio boolean NOT NULL DEFAULT false;

-- 2) Novo canal de integração. O token da integração WHATSAPP é o Bearer que o bot usa na
--    API /api/v1; callback_url recebe o aviso de "pedido pago" (assinado com segredo_hmac).
ALTER TABLE integracoes_webhook ADD COLUMN IF NOT EXISTS callback_url text;
ALTER TABLE integracoes_webhook DROP CONSTRAINT IF EXISTS integracoes_webhook_canal_check;
ALTER TABLE integracoes_webhook ADD CONSTRAINT integracoes_webhook_canal_check CHECK (canal IN ('ZEDELIVERY', 'WHATSAPP'));
ALTER TABLE webhooks_recebidos DROP CONSTRAINT IF EXISTS webhooks_recebidos_canal_check;
ALTER TABLE webhooks_recebidos ADD CONSTRAINT webhooks_recebidos_canal_check CHECK (canal IN ('ZEDELIVERY', 'WHATSAPP'));

-- 3) Se o schema base tiver CHECK em vendas.tipo_origem, inclui WHATSAPP (sem criar um novo).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'vendas_tipo_origem_check') THEN
        ALTER TABLE vendas DROP CONSTRAINT vendas_tipo_origem_check;
        ALTER TABLE vendas ADD CONSTRAINT vendas_tipo_origem_check CHECK (tipo_origem IN ('BALCAO', 'ZEDELIVERY', 'COMANDA', 'WHATSAPP'));
    END IF;
END $$;

-- 4) Pedido feito pelo bot. Só vira venda (baixa estoque) quando o Pix é confirmado pelo
--    webhook do Asaas; a cobrança leva externalReference = 'pedido:{id}'.
CREATE TABLE IF NOT EXISTS pedidos_online (
    id                uuid PRIMARY KEY,
    tenant_id         uuid NOT NULL REFERENCES tenants(id),
    cliente_nome      text NOT NULL,
    cliente_telefone  text NOT NULL,
    itens             jsonb NOT NULL,
    total             numeric(12,2) NOT NULL CHECK (total > 0),
    observacao        text,
    status            text NOT NULL DEFAULT 'AGUARDANDO_PAGAMENTO'
                      CHECK (status IN ('AGUARDANDO_PAGAMENTO', 'PAGO', 'PAGO_SEM_ESTOQUE', 'EXPIRADO', 'ERRO')),
    asaas_payment_id  text,
    pix_copia_cola    text,
    pix_expira_em     timestamptz,
    venda_id          uuid,
    erro              text,
    criado_em         timestamptz NOT NULL DEFAULT now(),
    pago_em           timestamptz
);
CREATE INDEX IF NOT EXISTS ix_pedidos_online_tenant ON pedidos_online (tenant_id, criado_em DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pedidos_online_payment ON pedidos_online (asaas_payment_id) WHERE asaas_payment_id IS NOT NULL;

ALTER TABLE pedidos_online ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON pedidos_online FROM anon, authenticated;
