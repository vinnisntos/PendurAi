using System.Data;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;
using Npgsql;

namespace EmporioGege.Application.Services
{
    public class VendaService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : IVendaService
    {
        private const int MaxTentativas = 3;

        public static readonly IReadOnlySet<string> MetodosPagamento = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DINHEIRO", "DEBITO", "CREDITO", "PIX", "FIADO" };

        public async Task<ResultadoVendaDto> FinalizarVendaAsync(FinalizarVendaDto dto, CancellationToken ct = default)
        {
            if (dto.Itens.Count == 0)
                throw new ArgumentException("A venda precisa ter ao menos um item.", nameof(dto));

            // Qualquer texto era gravado como forma de pagamento; só pedido do Zé Delivery chega sem ela.
            if (dto.MetodoPagamento is null ? dto.TipoOrigem != TipoOrigemVenda.ZeDelivery : !MetodosPagamento.Contains(dto.MetodoPagamento))
                throw new ArgumentException("Forma de pagamento inválida.", nameof(dto));

            var tenantId = tenantProvider.RequireTenantId();

            for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
            {
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

                try
                {
                    await connectionFactory.SetTenantContextAsync(connection, transaction, tenantId, ct);

                    // FOR SHARE bloqueia contra o FOR UPDATE do fechamento: sem isso uma venda que
                    // confirmava logo depois do fechamento entrava num turno FECHADO e o saldo não batia.
                    if (dto.TurnoId is { } turnoVenda)
                    {
                        var statusTurno = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                            "SELECT status FROM caixa_turnos WHERE id = @TurnoId AND tenant_id = @TenantId FOR SHARE",
                            new { TurnoId = turnoVenda, TenantId = tenantId }, transaction, cancellationToken: ct));
                        if (statusTurno != "ABERTO")
                            throw new TurnoInvalidoException("O turno de caixa foi fechado. Abra um novo turno para continuar vendendo.");
                    }

                    // Trava a comanda antes de baixar: um item lançado em outro terminal entre a leitura
                    // dos itens e este ponto ficaria numa comanda FECHADA sem ser cobrado.
                    if (dto.ComandaId is { } comandaVenda)
                    {
                        var atual = await connection.QuerySingleAsync<(long Linhas, long Quantidade)>(new CommandDefinition(
                            """
                            SELECT (SELECT count(*) FROM comandas_itens WHERE comanda_id = c.id AND tenant_id = c.tenant_id),
                                   (SELECT coalesce(sum(quantidade), 0) FROM comandas_itens WHERE comanda_id = c.id AND tenant_id = c.tenant_id)
                            FROM comandas c WHERE c.id = @ComandaId AND c.tenant_id = @TenantId FOR UPDATE
                            """,
                            new { ComandaId = comandaVenda, TenantId = tenantId }, transaction, cancellationToken: ct));
                        if (atual.Linhas != dto.Itens.Count || atual.Quantidade != dto.Itens.Sum(i => i.Quantidade))
                            throw new ComandaInvalidaException("A comanda foi alterada enquanto era fechada. Confira os itens e feche de novo.");
                    }

                    // Baixa todos os itens na MESMA transação: se um item não tiver estoque,
                    // EstoqueInsuficienteException derruba a transação inteira — sem baixa
                    // parcial do carrinho, diferente de chamar EstoqueService item a item.
                    var itensBaixados = new List<ResultadoBaixaEstoqueDto>();
                    foreach (var item in dto.Itens)
                    {
                        var baixaDto = new BaixaEstoqueDto(item.ProdutoId, item.Quantidade, dto.TipoOrigem, item.TipoPreco, dto.ReferenciaExterna);
                        itensBaixados.Add(await EstoqueOperacoes.DecrementarAsync(connection, transaction, tenantId, baixaDto, ct));
                    }

                    // Preço/custo vêm do produto lido dentro da transação (EstoqueOperacoes),
                    // nunca do cliente — evita venda finalizar com preço adulterado no request.
                    var totalVenda = dto.Itens.Zip(itensBaixados, (item, r) => r.PrecoUnitarioAplicado * item.Quantidade).Sum();
                    var totalCusto = dto.Itens.Zip(itensBaixados, (item, r) => r.CustoUnitarioAplicado * item.Quantidade).Sum();
                    var itensResultado = dto.Itens
                        .Zip(itensBaixados, (item, r) => new ItemVendaResultadoDto(item.ProdutoId, item.Quantidade, r.PrecoUnitarioAplicado, r.PrecoUnitarioAplicado * item.Quantidade))
                        .ToList();

                    // Fiado: trava a linha do cliente (FOR UPDATE) e checa o limite de crédito
                    // na MESMA transação da venda — se estourar, a venda inteira reverte junto
                    // com a baixa de estoque já feita acima (nada fica pela metade).
                    if (string.Equals(dto.MetodoPagamento, "FIADO", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dto.ClienteId is not { } clienteIdFiado)
                            throw new InvalidOperationException("Venda fiado precisa de um cliente selecionado.");

                        var cliente = await connection.QuerySingleOrDefaultAsync<ClienteCreditoRow>(new CommandDefinition(
                            "SELECT limite_credito AS LimiteCredito, saldo_devedor AS SaldoDevedor FROM clientes WHERE id = @ClienteId AND tenant_id = @TenantId FOR UPDATE",
                            new { ClienteId = clienteIdFiado, TenantId = tenantId }, transaction, cancellationToken: ct));

                        if (cliente is null)
                            throw new InvalidOperationException($"Cliente {clienteIdFiado} não encontrado.");

                        var novoSaldo = cliente.SaldoDevedor + totalVenda;
                        if (novoSaldo > cliente.LimiteCredito)
                            throw new LimiteCreditoExcedidoException(clienteIdFiado, cliente.LimiteCredito, cliente.SaldoDevedor, totalVenda);

                        await connection.ExecuteAsync(new CommandDefinition(
                            "UPDATE clientes SET saldo_devedor = @NovoSaldo WHERE id = @ClienteId AND tenant_id = @TenantId",
                            new { NovoSaldo = novoSaldo, ClienteId = clienteIdFiado, TenantId = tenantId }, transaction, cancellationToken: ct));
                    }

                    var vendaId = Guid.NewGuid();
                    var dataVenda = DateTime.UtcNow;
                    var tipoOrigemTexto = dto.TipoOrigem switch
                    {
                        TipoOrigemVenda.ZeDelivery => "ZEDELIVERY",
                        TipoOrigemVenda.Comanda => "COMANDA",
                        _ => "BALCAO"
                    };

                    await connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO vendas (id, tenant_id, turno_id, cliente_id, comanda_id, tipo_origem, status, emitir_nota_fiscal, total_custo, total_venda, metodo_pagamento, data_venda, referencia_externa)
                        VALUES (@Id, @TenantId, @TurnoId, @ClienteId, @ComandaId, @TipoOrigem, 'FECHADA', @EmitirNotaFiscal, @TotalCusto, @TotalVenda, @MetodoPagamento, @DataVenda, @ReferenciaExterna)
                        """,
                        new
                        {
                            Id = vendaId,
                            TenantId = tenantId,
                            dto.TurnoId,
                            dto.ClienteId,
                            dto.ComandaId,
                            TipoOrigem = tipoOrigemTexto,
                            dto.EmitirNotaFiscal,
                            TotalCusto = totalCusto,
                            TotalVenda = totalVenda,
                            dto.MetodoPagamento,
                            DataVenda = dataVenda,
                            dto.ReferenciaExterna
                        },
                        transaction, cancellationToken: ct));

                    foreach (var (item, resultado) in dto.Itens.Zip(itensBaixados))
                    {
                        var subtotal = resultado.PrecoUnitarioAplicado * item.Quantidade;
                        await connection.ExecuteAsync(new CommandDefinition(
                            """
                            INSERT INTO vendas_itens (id, tenant_id, venda_id, produto_id, quantidade, preco_unitario_aplicado, custo_unitario_aplicado, subtotal)
                            VALUES (gen_random_uuid(), @TenantId, @VendaId, @ProdutoId, @Quantidade, @PrecoUnitario, @CustoUnitario, @Subtotal)
                            """,
                            new
                            {
                                TenantId = tenantId,
                                VendaId = vendaId,
                                ProdutoId = item.ProdutoId,
                                item.Quantidade,
                                PrecoUnitario = resultado.PrecoUnitarioAplicado,
                                CustoUnitario = resultado.CustoUnitarioAplicado,
                                Subtotal = subtotal
                            },
                            transaction, cancellationToken: ct));
                    }

                    // Só credita o ledger (dinheiro físico no caixa) pra venda de balcão paga
                    // em dinheiro dentro de um turno aberto — cartão/Pix não passam pela
                    // gaveta física, e Zé Delivery não tem turno associado (TurnoId nulo).
                    if (dto.TurnoId is { } turnoId && string.Equals(dto.MetodoPagamento, "DINHEIRO", StringComparison.OrdinalIgnoreCase))
                    {
                        var lancamento = new LancamentoLedgerDto(turnoId, totalVenda, TipoOperacaoLedger.Credito, $"Venda {vendaId}");
                        await LedgerOperacoes.AdicionarAsync(connection, transaction, tenantId, lancamento, ct);
                    }

                    // Fechamento de comanda vira venda: marca a comanda FECHADA na MESMA
                    // transação da venda — se a comanda já não estiver mais ABERTA (fechada ou
                    // cancelada por outra requisição concorrente), a venda inteira reverte junto
                    // em vez de gravar uma venda "órfã" de uma comanda que já não existe mais.
                    if (dto.ComandaId is { } comandaIdFechada)
                    {
                        var linhasComanda = await connection.ExecuteAsync(new CommandDefinition(
                            "UPDATE comandas SET status = 'FECHADA', atualizado_em = now() WHERE id = @ComandaId AND tenant_id = @TenantId AND status = 'ABERTA'",
                            new { ComandaId = comandaIdFechada, TenantId = tenantId }, transaction, cancellationToken: ct));

                        if (linhasComanda == 0)
                            throw new InvalidOperationException($"Comanda {comandaIdFechada} não está aberta ou não existe.");
                    }

                    await transaction.CommitAsync(ct);
                    return new ResultadoVendaDto(vendaId, totalVenda, totalCusto, itensResultado);
                }
                catch (PostgresException ex) when (ex.SqlState == "40001" && tentativa < MaxTentativas)
                {
                    await transaction.RollbackAsync(ct);
                }
            }

            throw new InvalidOperationException($"Falha ao finalizar venda após {MaxTentativas} tentativas (conflitos de concorrência).");
        }

        private const string SelecaoResumo = """
            SELECT v.id AS Id, v.data_venda AS DataVenda, v.tipo_origem AS TipoOrigem, v.metodo_pagamento AS MetodoPagamento,
                   com.numero_comanda AS NumeroComanda, cli.nome AS ClienteNome, v.total_venda AS TotalVenda, v.total_custo AS TotalCusto
            FROM vendas v
            LEFT JOIN comandas com ON com.id = v.comanda_id
            LEFT JOIN clientes cli ON cli.id = v.cliente_id
            WHERE v.tenant_id = @TenantId AND v.status = 'FECHADA'
            """;

        public async Task<IReadOnlyList<VendaResumoDto>> ListarAsync(DateTime inicio, DateTime fimExclusivo, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<VendaResumoDto>(new CommandDefinition(
                $"{SelecaoResumo} AND v.data_venda >= @Inicio AND v.data_venda < @Fim ORDER BY v.data_venda DESC",
                new { TenantId = tenantId, Inicio = inicio, Fim = fimExclusivo }, cancellationToken: ct));

            return linhas.AsList();
        }

        public async Task<VendaDetalheDto?> ObterDetalheAsync(Guid id, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            var venda = await connection.QuerySingleOrDefaultAsync<VendaResumoDto>(new CommandDefinition(
                $"{SelecaoResumo} AND v.id = @Id",
                new { TenantId = tenantId, Id = id }, cancellationToken: ct));

            if (venda is null)
                return null;

            var itens = await connection.QueryAsync<ItemVendaDetalheDto>(new CommandDefinition(
                """
                SELECT p.nome AS ProdutoNome, vi.quantidade AS Quantidade, vi.preco_unitario_aplicado AS PrecoUnitarioAplicado, vi.subtotal AS Subtotal
                FROM vendas_itens vi
                JOIN produtos p ON p.id = vi.produto_id
                WHERE vi.tenant_id = @TenantId AND vi.venda_id = @VendaId
                """,
                new { TenantId = tenantId, VendaId = id }, cancellationToken: ct));

            return new VendaDetalheDto(
                venda.Id, venda.DataVenda, venda.TipoOrigem, venda.MetodoPagamento, venda.NumeroComanda,
                venda.ClienteNome, venda.TotalVenda, venda.TotalCusto, itens.AsList());
        }

        private sealed record ClienteCreditoRow(decimal LimiteCredito, decimal SaldoDevedor);
    }
}
