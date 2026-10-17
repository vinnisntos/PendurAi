using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Common;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class DashboardService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : IDashboardService
    {
        public async Task<DashboardResumoDto> ObterResumoAsync(DateTime inicio, DateTime fimExclusivo, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linha = await connection.QuerySingleAsync<ResumoRow>(new CommandDefinition(
                """
                SELECT
                    COALESCE(SUM(total_venda), 0) AS Faturamento,
                    COALESCE(SUM(total_custo), 0) AS Cmv,
                    COUNT(*)::int AS TotalVendas
                FROM vendas
                WHERE tenant_id = @TenantId AND status = 'FECHADA' AND data_venda >= @Inicio AND data_venda < @Fim
                """,
                new { TenantId = tenantId, Inicio = inicio, Fim = fimExclusivo }, cancellationToken: ct));

            var lucroBruto = linha.Faturamento - linha.Cmv;
            decimal? roi = linha.Cmv > 0 ? Math.Round(lucroBruto / linha.Cmv * 100, 1) : null;

            return new DashboardResumoDto(linha.Faturamento, linha.Cmv, lucroBruto, roi, linha.TotalVendas);
        }

        public async Task<int> ContarProdutosEstoqueCriticoAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleAsync<int>(new CommandDefinition(
                "SELECT COUNT(*)::int FROM produtos WHERE tenant_id = @TenantId AND ativo = true AND estoque_atual <= estoque_minimo",
                new { TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task<int> ContarComandasAtivasAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleAsync<int>(new CommandDefinition(
                "SELECT COUNT(*)::int FROM comandas WHERE tenant_id = @TenantId AND status = 'ABERTA'",
                new { TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task<decimal> ObterFiadoPendenteTotalAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleAsync<decimal>(new CommandDefinition(
                "SELECT COALESCE(SUM(saldo_devedor), 0) FROM clientes WHERE tenant_id = @TenantId",
                new { TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<ProdutoValidadeDto>> ListarProdutosProximosValidadeAsync(int diasLimite = 30, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            var hoje = FusoHorarioBrasil.HojeLocal();
            var limite = hoje.AddDays(diasLimite);

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<ProdutoValidadeRow>(new CommandDefinition(
                """
                SELECT id AS Id, nome AS Nome, data_validade AS DataValidade, estoque_atual AS EstoqueAtual
                FROM produtos
                WHERE tenant_id = @TenantId AND ativo = true AND data_validade IS NOT NULL AND data_validade <= @Limite AND estoque_atual > 0
                ORDER BY data_validade ASC
                """,
                new { TenantId = tenantId, Limite = limite }, cancellationToken: ct));

            return linhas
                .Select(l => new ProdutoValidadeDto(l.Id, l.Nome, l.DataValidade, l.DataValidade.DayNumber - hoje.DayNumber, l.EstoqueAtual))
                .ToList();
        }

        public async Task<IReadOnlyList<ProdutoMaisVendidoDto>> ListarProdutosMaisVendidosAsync(DateTime inicio, DateTime fimExclusivo, int top, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<ProdutoMaisVendidoDto>(new CommandDefinition(
                """
                SELECT p.id AS ProdutoId, p.nome AS Nome,
                       SUM(COALESCE(vi.quantidade_unidades, vi.quantidade))::int AS QuantidadeVendida, SUM(vi.subtotal) AS TotalVendido
                FROM vendas_itens vi
                JOIN vendas v ON v.id = vi.venda_id AND v.tenant_id = vi.tenant_id
                JOIN produtos p ON p.id = vi.produto_id AND p.tenant_id = vi.tenant_id
                WHERE vi.tenant_id = @TenantId AND v.status = 'FECHADA'
                      AND v.data_venda >= @Inicio AND v.data_venda < @Fim
                GROUP BY p.id, p.nome
                ORDER BY QuantidadeVendida DESC
                LIMIT @Top
                """,
                new { TenantId = tenantId, Inicio = inicio, Fim = fimExclusivo, Top = top }, cancellationToken: ct));

            return linhas.AsList();
        }

        private sealed record ResumoRow(decimal Faturamento, decimal Cmv, int TotalVendas);

        private sealed record ProdutoValidadeRow(Guid Id, string Nome, DateOnly DataValidade, int EstoqueAtual);
    }
}
