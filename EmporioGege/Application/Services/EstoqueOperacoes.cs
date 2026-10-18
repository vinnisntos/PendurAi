using System.Data.Common;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;

namespace EmporioGege.Application.Services
{
    // Lógica de baixa de estoque isolada de qualquer gerenciamento de transação —
    // opera dentro de uma conexão/transação que o chamador já abriu. Isso permite
    // ao EstoqueService baixar UM produto na própria transação (caso de uso do
    // webhook/chamadas avulsas) e ao VendaService baixar TODOS os itens de um
    // carrinho na MESMA transação (atomicidade real: se o item 3 de 5 não tiver
    // estoque, os itens 1-2 revertem junto, em vez do problema de baixa parcial
    // que existia quando cada item processava numa transação própria).
    internal static class EstoqueOperacoes
    {
        public static async Task<ResultadoBaixaEstoqueDto> DecrementarAsync(
            DbConnection connection, DbTransaction transaction, Guid tenantId, BaixaEstoqueDto dto, CancellationToken ct)
        {
            var produto = await connection.QuerySingleOrDefaultAsync<ProdutoRow>(
                new CommandDefinition(
                    """
                    SELECT estoque_atual AS EstoqueAtual, preco_venda_base AS PrecoVendaBase,
                           custo_medio AS CustoMedio, quantidade_por_caixa AS QuantidadePorCaixa
                    FROM produtos
                    WHERE tenant_id = @TenantId AND id = @ProdutoId
                    FOR UPDATE
                    """,
                    new { TenantId = tenantId, dto.ProdutoId },
                    transaction, cancellationToken: ct));

            if (produto is null)
                throw new InvalidOperationException($"Produto {dto.ProdutoId} não encontrado para o tenant {tenantId}.");

            // dto.Quantidade é em "unidades de venda" (ex.: 2 caixas); o estoque é sempre
            // controlado em unidades — por isso o fator de conversão entra aqui.
            var fatorConversao = dto.TipoPreco == TipoPreco.Caixa ? Math.Max(produto.QuantidadePorCaixa, 1) : 1;
            var quantidadeEstoque = dto.Quantidade * fatorConversao;

            if (produto.EstoqueAtual < quantidadeEstoque)
                throw new EstoqueInsuficienteException(dto.ProdutoId, quantidadeEstoque, produto.EstoqueAtual);

            var novoEstoque = produto.EstoqueAtual - quantidadeEstoque;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE produtos SET estoque_atual = @NovoEstoque
                    WHERE tenant_id = @TenantId AND id = @ProdutoId
                    """,
                    new { NovoEstoque = novoEstoque, TenantId = tenantId, dto.ProdutoId },
                    transaction, cancellationToken: ct));

            var origemDescricao = dto.TipoOrigem switch
            {
                TipoOrigemVenda.ZeDelivery => "ZEDELIVERY",
                TipoOrigemVenda.Comanda => "COMANDA",
                TipoOrigemVenda.WhatsApp => "WHATSAPP",
                _ => "BALCAO"
            };
            var justificativa = dto.ReferenciaExterna is null
                ? $"Origem: {origemDescricao}"
                : $"Origem: {origemDescricao} | Ref: {dto.ReferenciaExterna}";

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO estoque_movimentacoes (id, tenant_id, produto_id, tipo_movimentacao, quantidade, justificativa, data_movimento)
                    VALUES (gen_random_uuid(), @TenantId, @ProdutoId, 'SAIDA_VENDA', @Quantidade, @Justificativa, now())
                    """,
                    new { TenantId = tenantId, dto.ProdutoId, Quantidade = quantidadeEstoque, Justificativa = justificativa },
                    transaction, cancellationToken: ct));

            var precoUnitarioAplicado = await ResolverPrecoAsync(connection, transaction, tenantId, dto.ProdutoId, dto.TipoPreco, produto.PrecoVendaBase, fatorConversao, ct);
            var custoUnitarioAplicado = produto.CustoMedio * fatorConversao;

            return new ResultadoBaixaEstoqueDto(dto.ProdutoId, produto.EstoqueAtual, novoEstoque, precoUnitarioAplicado, custoUnitarioAplicado);
        }

        // Busca o preço diferenciado (precos_produto) pro tipo de venda; se o lojista ainda
        // não cadastrou um preço específico pra Caixa/Atacado, cai para preco_venda_base
        // (multiplicado pelo fator de conversão no caso de Caixa) em vez de bloquear a venda.
        private static async Task<decimal> ResolverPrecoAsync(
            DbConnection connection, DbTransaction transaction, Guid tenantId, Guid produtoId, TipoPreco tipoPreco, decimal precoVendaBase, int fatorConversao, CancellationToken ct)
        {
            if (tipoPreco == TipoPreco.Balcao)
                return precoVendaBase;

            var tipoPrecoTexto = tipoPreco == TipoPreco.Caixa ? "CAIXA" : "ATACADO";

            var precoConfigurado = await connection.QuerySingleOrDefaultAsync<decimal?>(
                new CommandDefinition(
                    """
                    SELECT valor FROM precos_produto
                    WHERE tenant_id = @TenantId AND produto_id = @ProdutoId AND tipo_preco = @TipoPreco
                    """,
                    new { TenantId = tenantId, ProdutoId = produtoId, TipoPreco = tipoPrecoTexto },
                    transaction, cancellationToken: ct));

            return precoConfigurado ?? precoVendaBase * fatorConversao;
        }

        private sealed record ProdutoRow(int EstoqueAtual, decimal PrecoVendaBase, decimal CustoMedio, int QuantidadePorCaixa);
    }
}
