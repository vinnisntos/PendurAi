using System.Data.Common;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;
using Npgsql;

namespace EmporioGege.Application.Services
{
    public class ComandaService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider, IVendaService vendaService) : IComandaService
    {
        private const string SelecaoResumo = """
            SELECT c.id AS Id, c.numero_comanda AS NumeroComanda, c.status AS Status,
                   c.created_at AS CreatedAt, c.atualizado_em AS AtualizadoEm,
                   COALESCE(i.total, 0) AS Total, COALESCE(i.qtd, 0)::int AS QuantidadeItens
            FROM comandas c
            LEFT JOIN (
                SELECT comanda_id, SUM(subtotal) AS total, COUNT(*) AS qtd
                FROM comandas_itens
                WHERE tenant_id = @TenantId
                GROUP BY comanda_id
            ) i ON i.comanda_id = c.id
            WHERE c.tenant_id = @TenantId
            """;

        public async Task<ComandaResumoDto> AbrirAsync(string numeroComanda, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(numeroComanda))
                throw new ComandaInvalidaException("Informe um número/identificação para a comanda.");

            var tenantId = tenantProvider.RequireTenantId();
            var comandaId = Guid.NewGuid();
            var agora = DateTime.UtcNow;
            var numeroTratado = numeroComanda.Trim();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO comandas (id, tenant_id, numero_comanda, status, created_at, atualizado_em)
                    VALUES (@Id, @TenantId, @NumeroComanda, 'ABERTA', @Agora, @Agora)
                    """,
                    new { Id = comandaId, TenantId = tenantId, NumeroComanda = numeroTratado, Agora = agora }, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                // Índice único parcial (0008_comandas_itens.sql): só 1 comanda ABERTA por número/tenant.
                throw new ComandaInvalidaException($"Já existe uma comanda aberta com o número \"{numeroTratado}\".");
            }

            return new ComandaResumoDto(comandaId, numeroTratado, "ABERTA", agora, agora, 0m, 0);
        }

        public async Task<IReadOnlyList<ComandaResumoDto>> ListarAbertasAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<ComandaResumoDto>(new CommandDefinition(
                $"{SelecaoResumo} AND c.status = 'ABERTA' ORDER BY c.created_at",
                new { TenantId = tenantId }, cancellationToken: ct));

            return linhas.AsList();
        }

        public async Task<IReadOnlyList<ComandaResumoDto>> ListarTodasAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<ComandaResumoDto>(new CommandDefinition(
                $"{SelecaoResumo} ORDER BY c.atualizado_em DESC",
                new { TenantId = tenantId }, cancellationToken: ct));

            return linhas.AsList();
        }

        public async Task<ComandaDetalheDto?> ObterDetalheAsync(Guid comandaId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            var comanda = await connection.QuerySingleOrDefaultAsync<ComandaRow>(new CommandDefinition(
                "SELECT id AS Id, numero_comanda AS NumeroComanda, status AS Status, created_at AS CreatedAt FROM comandas WHERE id = @ComandaId AND tenant_id = @TenantId",
                new { ComandaId = comandaId, TenantId = tenantId }, cancellationToken: ct));

            if (comanda is null)
                return null;

            var itens = await connection.QueryAsync<ItemComandaDto>(new CommandDefinition(
                """
                SELECT ci.id AS Id, ci.produto_id AS ProdutoId, p.nome AS ProdutoNome, ci.quantidade AS Quantidade,
                       ci.tipo_preco AS TipoPreco, ci.preco_unitario_aplicado AS PrecoUnitarioAplicado, ci.subtotal AS Subtotal
                FROM comandas_itens ci
                JOIN produtos p ON p.id = ci.produto_id
                WHERE ci.tenant_id = @TenantId AND ci.comanda_id = @ComandaId
                ORDER BY ci.criado_em
                """,
                new { TenantId = tenantId, ComandaId = comandaId }, cancellationToken: ct));

            var listaItens = itens.AsList();
            var total = listaItens.Sum(i => i.Subtotal);

            return new ComandaDetalheDto(comanda.Id, comanda.NumeroComanda, comanda.Status, comanda.CreatedAt, listaItens, total);
        }

        public async Task<ItemComandaDto> AdicionarItemAsync(Guid comandaId, Guid produtoId, int quantidade, TipoPreco tipoPreco, CancellationToken ct = default)
        {
            if (quantidade <= 0)
                throw new ArgumentException("Quantidade precisa ser maior que zero.", nameof(quantidade));

            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // FOR UPDATE: o fechamento trava a mesma linha, então o item não entra numa comanda já fechada.
            var comandaStatus = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT status FROM comandas WHERE id = @ComandaId AND tenant_id = @TenantId FOR UPDATE",
                new { ComandaId = comandaId, TenantId = tenantId }, transaction, cancellationToken: ct));

            if (comandaStatus is null)
                throw new ComandaInvalidaException($"Comanda {comandaId} não encontrada.");
            if (comandaStatus != "ABERTA")
                throw new ComandaInvalidaException("Só é possível adicionar itens a uma comanda aberta.");

            var produto = await connection.QuerySingleOrDefaultAsync<ProdutoRow>(new CommandDefinition(
                """
                SELECT nome AS Nome, preco_venda_base AS PrecoVendaBase, custo_medio AS CustoMedio,
                       quantidade_por_caixa AS QuantidadePorCaixa, estoque_atual AS EstoqueAtual
                FROM produtos
                WHERE id = @ProdutoId AND tenant_id = @TenantId AND ativo = true
                """,
                new { ProdutoId = produtoId, TenantId = tenantId }, cancellationToken: ct));

            if (produto is null)
                throw new ComandaInvalidaException($"Produto {produtoId} não encontrado ou inativo.");

            var fatorConversao = tipoPreco == TipoPreco.Caixa ? Math.Max(produto.QuantidadePorCaixa, 1) : 1;

            // Checagem só informativa pra UX (evita prometer um item visivelmente indisponível) —
            // não reserva nada de verdade: a baixa real de estoque (com FOR UPDATE) só acontece
            // no fechamento da comanda, via VendaService/EstoqueOperacoes, única fonte da verdade.
            if (quantidade * fatorConversao > produto.EstoqueAtual)
                throw new EstoqueInsuficienteException(produtoId, quantidade * fatorConversao, produto.EstoqueAtual);

            var precoUnitarioAplicado = await ResolverPrecoPreviaAsync(connection, tenantId, produtoId, tipoPreco, produto.PrecoVendaBase, fatorConversao, ct);
            var custoUnitarioAplicado = produto.CustoMedio * fatorConversao;
            var subtotal = precoUnitarioAplicado * quantidade;
            var itemId = Guid.NewGuid();
            var tipoPrecoTexto = tipoPreco switch { TipoPreco.Caixa => "CAIXA", TipoPreco.Atacado => "ATACADO", _ => "BALCAO" };

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO comandas_itens (id, tenant_id, comanda_id, produto_id, quantidade, tipo_preco, preco_unitario_aplicado, custo_unitario_aplicado, subtotal, criado_em)
                VALUES (@Id, @TenantId, @ComandaId, @ProdutoId, @Quantidade, @TipoPreco, @PrecoUnitarioAplicado, @CustoUnitarioAplicado, @Subtotal, now())
                """,
                new
                {
                    Id = itemId,
                    TenantId = tenantId,
                    ComandaId = comandaId,
                    ProdutoId = produtoId,
                    Quantidade = quantidade,
                    TipoPreco = tipoPrecoTexto,
                    PrecoUnitarioAplicado = precoUnitarioAplicado,
                    CustoUnitarioAplicado = custoUnitarioAplicado,
                    Subtotal = subtotal
                }, cancellationToken: ct));

            await TocarAtualizadoEmAsync(connection, tenantId, comandaId, ct);
            await transaction.CommitAsync(ct); // Npgsql roda os comandos acima na transação aberta da conexão

            return new ItemComandaDto(itemId, produtoId, produto.Nome, quantidade, tipoPrecoTexto, precoUnitarioAplicado, subtotal);
        }

        public async Task RemoverItemAsync(Guid comandaId, Guid itemId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            var comandaStatus = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT status FROM comandas WHERE id = @ComandaId AND tenant_id = @TenantId",
                new { ComandaId = comandaId, TenantId = tenantId }, cancellationToken: ct));

            if (comandaStatus is null)
                throw new ComandaInvalidaException($"Comanda {comandaId} não encontrada.");
            if (comandaStatus != "ABERTA")
                throw new ComandaInvalidaException("Só é possível remover itens de uma comanda aberta.");

            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM comandas_itens WHERE id = @ItemId AND comanda_id = @ComandaId AND tenant_id = @TenantId",
                new { ItemId = itemId, ComandaId = comandaId, TenantId = tenantId }, cancellationToken: ct));

            if (linhasAfetadas == 0)
                throw new ComandaInvalidaException($"Item {itemId} não encontrado na comanda {comandaId}.");

            await TocarAtualizadoEmAsync(connection, tenantId, comandaId, ct);
        }

        public async Task<ResultadoVendaDto> FecharAsync(Guid comandaId, Guid? turnoId, string metodoPagamento, bool emitirNotaFiscal, Guid? clienteId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            var comandaStatus = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT status FROM comandas WHERE id = @ComandaId AND tenant_id = @TenantId",
                new { ComandaId = comandaId, TenantId = tenantId }, cancellationToken: ct));

            if (comandaStatus is null)
                throw new ComandaInvalidaException($"Comanda {comandaId} não encontrada.");
            if (comandaStatus != "ABERTA")
                throw new ComandaInvalidaException("Comanda já foi fechada ou cancelada.");

            var itensComanda = await connection.QueryAsync<ItemComandaRow>(new CommandDefinition(
                "SELECT produto_id AS ProdutoId, quantidade AS Quantidade, tipo_preco AS TipoPreco, preco_unitario_aplicado AS PrecoUnitarioAplicado FROM comandas_itens WHERE tenant_id = @TenantId AND comanda_id = @ComandaId",
                new { TenantId = tenantId, ComandaId = comandaId }, cancellationToken: ct));

            var listaItens = itensComanda.AsList();
            if (listaItens.Count == 0)
                throw new ComandaInvalidaException("Comanda sem itens não pode ser fechada. Cancele-a se não houve consumo.");

            var itensVenda = listaItens
                .Select(i => new ItemVendaDto(i.ProdutoId, i.Quantidade, i.TipoPreco switch { "CAIXA" => TipoPreco.Caixa, "ATACADO" => TipoPreco.Atacado, _ => TipoPreco.Balcao }, i.PrecoUnitarioAplicado))
                .ToList();

            // A baixa de estoque real + gravação de vendas/vendas_itens acontece aqui dentro,
            // numa transação SERIALIZABLE própria (VendaService), que também marca esta comanda
            // como FECHADA na mesma transação — tudo ou nada, igual ao resto do sistema.
            return await vendaService.FinalizarVendaAsync(
                new FinalizarVendaDto(turnoId, itensVenda, TipoOrigemVenda.Comanda, emitirNotaFiscal, metodoPagamento, clienteId, comandaId), ct);
        }

        public async Task CancelarAsync(Guid comandaId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE comandas SET status = 'CANCELADA', atualizado_em = now() WHERE id = @ComandaId AND tenant_id = @TenantId AND status = 'ABERTA'",
                new { ComandaId = comandaId, TenantId = tenantId }, cancellationToken: ct));

            if (linhasAfetadas == 0)
                throw new ComandaInvalidaException("Comanda não está aberta ou não existe.");
        }

        private static Task TocarAtualizadoEmAsync(DbConnection connection, Guid tenantId, Guid comandaId, CancellationToken ct) =>
            connection.ExecuteAsync(new CommandDefinition(
                "UPDATE comandas SET atualizado_em = now() WHERE id = @ComandaId AND tenant_id = @TenantId",
                new { ComandaId = comandaId, TenantId = tenantId }, cancellationToken: ct));

        // Prévia de preço pra exibir o total corrente da comanda antes do fechamento — mesma
        // regra de fallback de EstoqueOperacoes.ResolverPrecoAsync (precos_produto > base), mas
        // sem transação/lock: aqui não estamos decrementando estoque, só exibindo uma estimativa.
        // O preço definitivo é sempre recalculado no fechamento, dentro da transação da venda.
        private static async Task<decimal> ResolverPrecoPreviaAsync(
            DbConnection connection, Guid tenantId, Guid produtoId, TipoPreco tipoPreco, decimal precoVendaBase, int fatorConversao, CancellationToken ct)
        {
            if (tipoPreco == TipoPreco.Balcao)
                return precoVendaBase;

            var tipoPrecoTexto = tipoPreco == TipoPreco.Caixa ? "CAIXA" : "ATACADO";

            var precoConfigurado = await connection.QuerySingleOrDefaultAsync<decimal?>(new CommandDefinition(
                "SELECT valor FROM precos_produto WHERE tenant_id = @TenantId AND produto_id = @ProdutoId AND tipo_preco = @TipoPreco",
                new { TenantId = tenantId, ProdutoId = produtoId, TipoPreco = tipoPrecoTexto }, cancellationToken: ct));

            return precoConfigurado ?? precoVendaBase * fatorConversao;
        }

        private sealed record ComandaRow(Guid Id, string NumeroComanda, string Status, DateTime CreatedAt);

        private sealed record ProdutoRow(string Nome, decimal PrecoVendaBase, decimal CustoMedio, int QuantidadePorCaixa, int EstoqueAtual);

        private sealed record ItemComandaRow(Guid ProdutoId, int Quantidade, string TipoPreco, decimal PrecoUnitarioAplicado);
    }
}
