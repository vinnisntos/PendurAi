using System.Data.Common;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class ProdutoService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : IProdutoService
    {
        private const string Selecao = """
            SELECT
                p.id AS Id, p.nome AS Nome, p.codigo_barras AS CodigoBarras, p.custo_medio AS CustoMedio,
                p.preco_venda_base AS PrecoVendaBase, p.estoque_atual AS EstoqueAtual, p.estoque_minimo AS EstoqueMinimo,
                p.unidade_medida AS UnidadeMedida, p.quantidade_por_caixa AS QuantidadePorCaixa,
                p.data_validade AS DataValidade, p.ativo AS Ativo,
                (SELECT valor FROM precos_produto WHERE tenant_id = p.tenant_id AND produto_id = p.id AND tipo_preco = 'CAIXA') AS PrecoCaixa,
                (SELECT valor FROM precos_produto WHERE tenant_id = p.tenant_id AND produto_id = p.id AND tipo_preco = 'ATACADO') AS PrecoAtacado,
                p.codigo_ncm AS CodigoNcm, p.cfop AS Cfop, p.permite_baixa_manual AS PermiteBaixaManual
            FROM produtos p
            """;

        public async Task<IReadOnlyList<ProdutoDetalheDto>> ListarAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<ProdutoDetalheDto>(new CommandDefinition(
                $"{Selecao} WHERE p.tenant_id = @TenantId ORDER BY p.nome",
                new { TenantId = tenantId }, cancellationToken: ct));

            return linhas.AsList();
        }

        public async Task<ProdutoDetalheDto?> ObterAsync(Guid id, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<ProdutoDetalheDto>(new CommandDefinition(
                $"{Selecao} WHERE p.tenant_id = @TenantId AND p.id = @Id",
                new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        }

        public async Task<Guid> SalvarAsync(SalvarProdutoDto dto, Guid usuarioId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            var produtoId = dto.Id ?? Guid.NewGuid();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // Código repetido na mesma loja fazia a busca por código (PDV, Zé Delivery) lançar exceção.
            if (!string.IsNullOrWhiteSpace(dto.CodigoBarras))
            {
                var outro = await connection.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
                    "SELECT nome FROM produtos WHERE tenant_id = @TenantId AND codigo_barras = @CodigoBarras AND id <> @Id LIMIT 1",
                    new { TenantId = tenantId, dto.CodigoBarras, Id = produtoId }, transaction, cancellationToken: ct));
                if (outro is not null)
                    throw new InvalidOperationException($"O código de barras {dto.CodigoBarras} já está no produto \"{outro}\".");
            }

            if (dto.Id is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO produtos (id, tenant_id, nome, codigo_barras, custo_medio, preco_venda_base, estoque_atual, estoque_minimo, unidade_medida, quantidade_por_caixa, data_validade, ativo, codigo_ncm, cfop, permite_baixa_manual, created_at)
                    VALUES (@Id, @TenantId, @Nome, @CodigoBarras, @CustoMedio, @PrecoVendaBase, @EstoqueAtual, @EstoqueMinimo, @UnidadeMedida, @QuantidadePorCaixa, @DataValidade, true, @CodigoNcm, @Cfop, @PermiteBaixaManual, now())
                    """,
                    new
                    {
                        Id = produtoId, TenantId = tenantId, dto.Nome, dto.CodigoBarras, dto.CustoMedio, dto.PrecoVendaBase,
                        dto.EstoqueAtual, dto.EstoqueMinimo, dto.UnidadeMedida, dto.QuantidadePorCaixa, dto.DataValidade,
                        dto.CodigoNcm, dto.Cfop, dto.PermiteBaixaManual
                    },
                    transaction, cancellationToken: ct));
            }
            else
            {
                // Lido ANTES do update pra poder calcular o delta e logar como AJUSTE_MANUAL
                // em estoque_movimentacoes - sem isso, editar o número direto no cadastro
                // (único jeito de corrigir estoque antes desta sessão) não deixava rastro
                // nenhum de quando/quanto/quem mudou.
                var estoqueAnterior = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                    "SELECT estoque_atual FROM produtos WHERE id = @Id AND tenant_id = @TenantId FOR UPDATE",
                    new { Id = produtoId, TenantId = tenantId }, transaction, cancellationToken: ct));

                if (estoqueAnterior is null)
                    throw new InvalidOperationException($"Produto {produtoId} não encontrado para o tenant {tenantId}.");

                // Vendas feitas enquanto a tela estava aberta eram apagadas do saldo: aplica só o que
                // o usuário mudou em relação ao valor que ele viu, em cima do saldo atual do banco.
                var deltaEstoque = dto.EstoqueAtual - (dto.EstoqueOriginal ?? estoqueAnterior.Value);
                var novoEstoque = estoqueAnterior.Value + deltaEstoque;

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE produtos SET
                        nome = @Nome, codigo_barras = @CodigoBarras, custo_medio = @CustoMedio, preco_venda_base = @PrecoVendaBase,
                        estoque_atual = @NovoEstoque, estoque_minimo = @EstoqueMinimo, unidade_medida = @UnidadeMedida,
                        quantidade_por_caixa = @QuantidadePorCaixa, data_validade = @DataValidade,
                        codigo_ncm = @CodigoNcm, cfop = @Cfop, permite_baixa_manual = @PermiteBaixaManual
                    WHERE id = @Id AND tenant_id = @TenantId
                    """,
                    new
                    {
                        Id = produtoId, TenantId = tenantId, dto.Nome, dto.CodigoBarras, dto.CustoMedio, dto.PrecoVendaBase,
                        NovoEstoque = novoEstoque, dto.EstoqueMinimo, dto.UnidadeMedida, dto.QuantidadePorCaixa, dto.DataValidade,
                        dto.CodigoNcm, dto.Cfop, dto.PermiteBaixaManual
                    },
                    transaction, cancellationToken: ct));

                if (deltaEstoque != 0)
                {
                    await EstoqueMovimentacaoOperacoes.RegistrarAsync(
                        connection, transaction, tenantId, produtoId, "AJUSTE_MANUAL", deltaEstoque,
                        "Ajuste manual via cadastro de produto", usuarioId, ct);
                }
            }

            await SalvarPrecoDiferenciadoAsync(connection, transaction, tenantId, produtoId, "CAIXA", dto.PrecoCaixa, ct);
            await SalvarPrecoDiferenciadoAsync(connection, transaction, tenantId, produtoId, "ATACADO", dto.PrecoAtacado, ct);

            await transaction.CommitAsync(ct);
            return produtoId;
        }

        public async Task DefinirAtivoAsync(Guid id, bool ativo, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE produtos SET ativo = @Ativo WHERE id = @Id AND tenant_id = @TenantId",
                new { Ativo = ativo, Id = id, TenantId = tenantId }, cancellationToken: ct));
        }

        // Sem valor configurado (@Valor null) = remove o preço diferenciado, voltando pro
        // fallback (preco_venda_base × fator de conversão) já calculado em EstoqueOperacoes.
        private static async Task SalvarPrecoDiferenciadoAsync(
            DbConnection connection, DbTransaction transaction, Guid tenantId, Guid produtoId, string tipoPreco, decimal? valor, CancellationToken ct)
        {
            if (valor is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM precos_produto WHERE tenant_id = @TenantId AND produto_id = @ProdutoId AND tipo_preco = @TipoPreco",
                    new { TenantId = tenantId, ProdutoId = produtoId, TipoPreco = tipoPreco }, transaction, cancellationToken: ct));
                return;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO precos_produto (id, tenant_id, produto_id, tipo_preco, valor, created_at)
                VALUES (gen_random_uuid(), @TenantId, @ProdutoId, @TipoPreco, @Valor, now())
                ON CONFLICT (tenant_id, produto_id, tipo_preco) DO UPDATE SET valor = EXCLUDED.valor
                """,
                new { TenantId = tenantId, ProdutoId = produtoId, TipoPreco = tipoPreco, Valor = valor }, transaction, cancellationToken: ct));
        }
    }
}
