-- =============================================================================
-- Migration 0017. Idempotente. Aplicar ANTES do deploy do código que a usa.
-- =============================================================================

-- 1) Quantidade do item em UNIDADES de estoque (uma caixa com 12 = 12). Sem isso o
--    ranking de mais vendidos somava caixas e unidades como se fossem a mesma coisa.
--    Itens antigos ficam nulos e o ranking usa a quantidade original como antes.
ALTER TABLE vendas_itens ADD COLUMN IF NOT EXISTS quantidade_unidades integer;

-- 2) Eventos do webhook do Asaas já processados: reenviar um evento antigo (replay) não
--    reativa nem suspende loja de novo.
CREATE TABLE IF NOT EXISTS asaas_eventos (
    id           text PRIMARY KEY,
    evento       text NOT NULL,
    recebido_em  timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE asaas_eventos ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON asaas_eventos FROM anon, authenticated;
