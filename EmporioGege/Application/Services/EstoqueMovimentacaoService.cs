using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class EstoqueMovimentacaoService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : IEstoqueMovimentacaoService
    {
        public async Task RegistrarEntradaAsync(Guid produtoId, int quantidade, string? justificativa, Guid usuarioId, decimal? custoUnitario = null, CancellationToken ct = default)
        {
            if (quantidade <= 0)
                throw new ArgumentException("Quantidade de entrada precisa ser maior que zero.", nameof(quantidade));
            if (custoUnitario < 0)
                throw new ArgumentException("Custo unitário não pode ser negativo.", nameof(custoUnitario));

            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // Incremento puro (col = col + X) é atômico por natureza no Postgres - diferente
            // da baixa de venda (EstoqueOperacoes), não precisa de SELECT ... FOR UPDATE antes,
            // já que não há decisão de negócio (ex.: checar estoque insuficiente) dependendo
            // do valor lido primeiro.
            // Com o custo da compra, recalcula o custo médio ponderado (base do CMV/lucro do dashboard);
            // sem ele, o custo médio só mudava à mão no cadastro e o lucro ficava errado.
            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE produtos SET
                    custo_medio = CASE WHEN @CustoUnitario IS NULL THEN custo_medio
                                       ELSE round((GREATEST(estoque_atual, 0) * custo_medio + @Quantidade * @CustoUnitario::numeric)
                                                  / (GREATEST(estoque_atual, 0) + @Quantidade), 4) END,
                    estoque_atual = estoque_atual + @Quantidade
                WHERE id = @ProdutoId AND tenant_id = @TenantId
                """,
                new { Quantidade = quantidade, CustoUnitario = custoUnitario, ProdutoId = produtoId, TenantId = tenantId }, transaction, cancellationToken: ct));

            if (linhasAfetadas == 0)
                throw new InvalidOperationException($"Produto {produtoId} não encontrado para o tenant {tenantId}.");

            await EstoqueMovimentacaoOperacoes.RegistrarAsync(
                connection, transaction, tenantId, produtoId, "ENTRADA_COMPRA", quantidade, justificativa, usuarioId, ct);

            await transaction.CommitAsync(ct);
        }

        public async Task RegistrarBaixaManualAsync(Guid produtoId, int quantidade, string motivo, string justificativa, Guid usuarioId, CancellationToken ct = default)
        {
            if (quantidade <= 0)
                throw new ArgumentException("Quantidade da baixa precisa ser maior que zero.", nameof(quantidade));

            if (string.IsNullOrWhiteSpace(justificativa))
                throw new ArgumentException("Justificativa é obrigatória pra baixa manual de estoque.", nameof(justificativa));

            var tipoMovimentacao = motivo switch
            {
                "QUEBRA" => "SAIDA_QUEBRA",
                "DESCARTE" => "SAIDA_DESCARTE",
                "USO_INSUMO" => "SAIDA_USO_INSUMO",
                _ => throw new ArgumentException($"Motivo inválido: {motivo}.", nameof(motivo))
            };

            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // Mesmo padrão de EstoqueOperacoes.DecrementarAsync: SELECT ... FOR UPDATE antes de
            // decidir (checar elegibilidade + saldo), já que aqui há decisão de negócio
            // dependendo do valor lido primeiro - diferente da entrada, que é incremento puro.
            var produto = await connection.QuerySingleOrDefaultAsync<ProdutoRow>(new CommandDefinition(
                "SELECT estoque_atual AS EstoqueAtual, permite_baixa_manual AS PermiteBaixaManual FROM produtos WHERE id = @ProdutoId AND tenant_id = @TenantId FOR UPDATE",
                new { ProdutoId = produtoId, TenantId = tenantId }, transaction, cancellationToken: ct));

            if (produto is null)
                throw new InvalidOperationException($"Produto {produtoId} não encontrado para o tenant {tenantId}.");

            if (!produto.PermiteBaixaManual)
                throw new InvalidOperationException("Este produto não está habilitado para baixa manual de estoque.");

            if (produto.EstoqueAtual < quantidade)
                throw new EstoqueInsuficienteException(produtoId, quantidade, produto.EstoqueAtual);

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE produtos SET estoque_atual = estoque_atual - @Quantidade WHERE id = @ProdutoId AND tenant_id = @TenantId",
                new { Quantidade = quantidade, ProdutoId = produtoId, TenantId = tenantId }, transaction, cancellationToken: ct));

            await EstoqueMovimentacaoOperacoes.RegistrarAsync(
                connection, transaction, tenantId, produtoId, tipoMovimentacao, quantidade, justificativa, usuarioId, ct);

            await transaction.CommitAsync(ct);
        }

        private sealed record ProdutoRow(int EstoqueAtual, bool PermiteBaixaManual);

        public async Task<IReadOnlyList<MovimentacaoEstoqueDto>> ListarHistoricoAsync(
            DateTime inicio, DateTime fimExclusivo, Guid? produtoId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<MovimentacaoEstoqueDto>(new CommandDefinition(
                """
                SELECT em.id AS Id, em.data_movimento AS DataMovimento, p.nome AS ProdutoNome,
                       em.tipo_movimentacao AS TipoMovimentacao, em.quantidade AS Quantidade,
                       em.justificativa AS Justificativa, perfil.nome AS UsuarioNome
                FROM estoque_movimentacoes em
                JOIN produtos p ON p.id = em.produto_id AND p.tenant_id = em.tenant_id
                LEFT JOIN profiles perfil ON perfil.id = em.usuario_id
                WHERE em.tenant_id = @TenantId AND em.data_movimento >= @Inicio AND em.data_movimento < @Fim
                      AND (@ProdutoId IS NULL OR em.produto_id = @ProdutoId)
                ORDER BY em.data_movimento DESC
                """,
                new { TenantId = tenantId, Inicio = inicio, Fim = fimExclusivo, ProdutoId = produtoId }, cancellationToken: ct));

            return linhas.AsList();
        }
    }
}
