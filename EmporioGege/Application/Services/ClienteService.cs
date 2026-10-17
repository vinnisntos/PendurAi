using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class ClienteService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : IClienteService
    {
        private const string Selecao = """
            SELECT id AS Id, nome AS Nome, telefone AS Telefone, cpf_rg AS CpfRg, limite_credito AS LimiteCredito, saldo_devedor AS SaldoDevedor
            FROM clientes
            """;

        public async Task<IReadOnlyList<ClienteDto>> ListarAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<ClienteDto>(new CommandDefinition(
                $"{Selecao} WHERE tenant_id = @TenantId ORDER BY nome",
                new { TenantId = tenantId }, cancellationToken: ct));

            return linhas.AsList();
        }

        public async Task<ClienteDto?> ObterAsync(Guid id, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<ClienteDto>(new CommandDefinition(
                $"{Selecao} WHERE tenant_id = @TenantId AND id = @Id",
                new { TenantId = tenantId, Id = id }, cancellationToken: ct));
        }

        public async Task<Guid> SalvarAsync(SalvarClienteDto dto, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            var clienteId = dto.Id ?? Guid.NewGuid();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            if (dto.Id is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO clientes (id, tenant_id, nome, telefone, cpf_rg, limite_credito, saldo_devedor, created_at)
                    VALUES (@Id, @TenantId, @Nome, @Telefone, @CpfRg, @LimiteCredito, 0, now())
                    """,
                    new { Id = clienteId, TenantId = tenantId, dto.Nome, dto.Telefone, dto.CpfRg, dto.LimiteCredito }, cancellationToken: ct));
            }
            else
            {
                var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE clientes SET nome = @Nome, telefone = @Telefone, cpf_rg = @CpfRg, limite_credito = @LimiteCredito WHERE id = @Id AND tenant_id = @TenantId",
                    new { Id = clienteId, TenantId = tenantId, dto.Nome, dto.Telefone, dto.CpfRg, dto.LimiteCredito }, cancellationToken: ct));

                if (linhasAfetadas == 0)
                    throw new InvalidOperationException($"Cliente {clienteId} não encontrado para o tenant {tenantId}.");
            }

            return clienteId;
        }

        public static readonly IReadOnlySet<string> FormasPagamentoFiado = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DINHEIRO", "PIX", "DEBITO", "CREDITO" };

        public async Task RegistrarPagamentoAsync(Guid id, decimal valor, string formaPagamento, Guid usuarioId, CancellationToken ct = default)
        {
            if (valor <= 0)
                throw new InvalidOperationException("Informe um valor de pagamento maior que zero.");
            if (!FormasPagamentoFiado.Contains(formaPagamento))
                throw new InvalidOperationException("Forma de pagamento inválida.");
            formaPagamento = formaPagamento.ToUpperInvariant();

            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var cliente = await connection.QuerySingleOrDefaultAsync<(string Nome, decimal SaldoDevedor)?>(new CommandDefinition(
                "SELECT nome, saldo_devedor FROM clientes WHERE id = @Id AND tenant_id = @TenantId FOR UPDATE",
                new { Id = id, TenantId = tenantId }, transaction, cancellationToken: ct))
                ?? throw new InvalidOperationException("Cliente não encontrado.");

            // Antes o GREATEST(…, 0) engolia o valor pago a mais sem avisar ninguém.
            if (valor > cliente.SaldoDevedor)
                throw new InvalidOperationException(
                    $"O valor é maior que a dívida do cliente ({cliente.SaldoDevedor.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))}).");

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE clientes SET saldo_devedor = saldo_devedor - @Valor WHERE id = @Id AND tenant_id = @TenantId",
                new { Valor = valor, Id = id, TenantId = tenantId }, transaction, cancellationToken: ct));

            // Dinheiro recebido entra na gaveta: credita o turno aberto de quem recebeu, senão o
            // fechamento acusava sobra de caixa.
            Guid? turnoId = null;
            if (formaPagamento == "DINHEIRO")
            {
                turnoId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM caixa_turnos WHERE tenant_id = @TenantId AND usuario_id = @UsuarioId AND status = 'ABERTO' FOR SHARE",
                    new { TenantId = tenantId, UsuarioId = usuarioId }, transaction, cancellationToken: ct));
                if (turnoId is { } turno)
                    await LedgerOperacoes.AdicionarAsync(connection, transaction, tenantId,
                        new LancamentoLedgerDto(turno, valor, Core.Enums.TipoOperacaoLedger.Credito, $"Pagamento de fiado: {cliente.Nome}"), ct);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO clientes_pagamentos (id, tenant_id, cliente_id, valor, forma_pagamento, usuario_id, turno_id)
                VALUES (gen_random_uuid(), @TenantId, @ClienteId, @Valor, @Forma, @UsuarioId, @TurnoId)
                """,
                new { TenantId = tenantId, ClienteId = id, Valor = valor, Forma = formaPagamento, UsuarioId = usuarioId, TurnoId = turnoId },
                transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
        }

        public async Task<IReadOnlyList<PagamentoFiadoDto>> ListarPagamentosAsync(Guid clienteId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var linhas = await connection.QueryAsync<PagamentoFiadoDto>(new CommandDefinition(
                """
                SELECT cp.criado_em AS CriadoEm, cp.valor AS Valor, cp.forma_pagamento AS FormaPagamento, p.nome AS UsuarioNome
                FROM clientes_pagamentos cp
                LEFT JOIN profiles p ON p.id = cp.usuario_id
                WHERE cp.tenant_id = @TenantId AND cp.cliente_id = @ClienteId
                ORDER BY cp.criado_em DESC
                LIMIT 50
                """,
                new { TenantId = tenantId, ClienteId = clienteId }, cancellationToken: ct));
            return linhas.AsList();
        }
    }
}
