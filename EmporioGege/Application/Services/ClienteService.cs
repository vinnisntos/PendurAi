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

        public async Task RegistrarPagamentoAsync(Guid id, decimal valor, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            if (valor <= 0)
                throw new InvalidOperationException("Informe um valor de pagamento maior que zero.");

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);

            // Antes o GREATEST(…, 0) engolia o valor pago a mais sem avisar ninguém.
            var linhasAfetadas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE clientes SET saldo_devedor = saldo_devedor - @Valor WHERE id = @Id AND tenant_id = @TenantId AND saldo_devedor >= @Valor",
                new { Valor = valor, Id = id, TenantId = tenantId }, cancellationToken: ct));

            if (linhasAfetadas == 0)
            {
                var saldo = await connection.QuerySingleOrDefaultAsync<decimal?>(new CommandDefinition(
                    "SELECT saldo_devedor FROM clientes WHERE id = @Id AND tenant_id = @TenantId",
                    new { Id = id, TenantId = tenantId }, cancellationToken: ct))
                    ?? throw new InvalidOperationException("Cliente não encontrado.");
                throw new InvalidOperationException(
                    $"O valor é maior que a dívida do cliente ({saldo.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))}).");
            }
        }
    }
}
