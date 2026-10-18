-- =============================================================================
-- Migration 0020: índice da reserva de estoque dos pedidos do bot (ReservaEstoque.cs).
-- Idempotente. Aplicar ANTES do deploy do código que a usa (branch feat/canal-whatsapp).
-- =============================================================================

-- Toda listagem de cardápio e todo pedido novo somam os pedidos AGUARDANDO_PAGAMENTO da loja;
-- índice parcial só com eles (pagos/expirados são a imensa maioria e não interessam aqui).
CREATE INDEX IF NOT EXISTS ix_pedidos_online_aguardando
    ON pedidos_online (tenant_id, criado_em)
    WHERE status = 'AGUARDANDO_PAGAMENTO';
