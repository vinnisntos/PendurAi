-- =============================================================================
-- Migration 0019: cardápio online público (/c/{slug}) + fotos dos produtos.
-- Idempotente. Aplicar ANTES do deploy do código que a usa (branch feat/canal-whatsapp).
-- =============================================================================

-- 1) Foto no próprio banco (servida por /cardapio/foto/{produto_id}): sem bucket externo e
--    sem abrir a CSP pra outro domínio. Limite de 2 MB por foto, validado no app.
CREATE TABLE IF NOT EXISTS produtos_fotos (
    produto_id     uuid PRIMARY KEY REFERENCES produtos(id) ON DELETE CASCADE,
    tenant_id      uuid NOT NULL REFERENCES tenants(id),
    conteudo       bytea NOT NULL,
    content_type   text NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    atualizado_em  timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE produtos_fotos ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON produtos_fotos FROM anon, authenticated;

-- 2) Endereço público do cardápio e número do WhatsApp que recebe o /comprar.
ALTER TABLE tenants ADD COLUMN IF NOT EXISTS cardapio_slug     text;
ALTER TABLE tenants ADD COLUMN IF NOT EXISTS cardapio_whatsapp text;
CREATE UNIQUE INDEX IF NOT EXISTS ux_tenants_cardapio_slug ON tenants (cardapio_slug) WHERE cardapio_slug IS NOT NULL;
