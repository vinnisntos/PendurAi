-- =============================================================================
-- Migration 0015: garante RLS nas tabelas criadas antes das migrations.
--
-- A 0004 deixou como "verificar manualmente" se tenants, clientes (CPF/RG),
-- vendas etc. tinham RLS. Sem RLS, a chave anon (pública, está no
-- appsettings.json) lê e grava essas tabelas direto pela API REST do Supabase.
--
-- O app não usa o cliente Supabase/PostgREST para nenhuma destas tabelas:
-- tudo passa pela conexão Npgsql/Dapper como 'postgres', que ignora RLS.
-- O cliente Supabase só faz login e lê o próprio profile (coberto pela 0006,
-- por isso profiles, produtos e precos_produto ficam de fora aqui).
--
-- Rodar manualmente no SQL Editor do Supabase. Idempotente; ignora tabela que
-- não existir.
-- =============================================================================
DO $$
DECLARE
    tabela text;
BEGIN
    FOREACH tabela IN ARRAY ARRAY[
        'tenants', 'clientes', 'comandas', 'comandas_itens', 'vendas', 'vendas_itens',
        'estoque_movimentacoes', 'caixa_turnos', 'caixa_movimentacoes', 'produtos_variacoes'
    ]
    LOOP
        IF to_regclass('public.' || tabela) IS NOT NULL THEN
            EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', tabela);
            EXECUTE format('REVOKE ALL ON public.%I FROM anon, authenticated', tabela);
        END IF;
    END LOOP;
END $$;

-- Conferência: deve voltar vazio (nenhuma tabela pública sem RLS).
SELECT c.relname AS tabela_sem_rls
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'r' AND NOT c.relrowsecurity
ORDER BY 1;
