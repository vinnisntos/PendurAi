using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Application.Services
{
    public class TurnoService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : ITurnoService
    {
        public async Task<TurnoAtualDto?> ObterTurnoAbertoAsync(Guid usuarioId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<TurnoAtualDto>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, data_abertura AS DataAbertura, saldo_inicial AS SaldoInicial, status AS Status
                    FROM caixa_turnos
                    WHERE tenant_id = @TenantId AND usuario_id = @UsuarioId AND status = 'ABERTO'
                    ORDER BY data_abertura DESC
                    LIMIT 1
                    """,
                    new { TenantId = tenantId, UsuarioId = usuarioId },
                    cancellationToken: ct));
        }

        public async Task<TurnoAtualDto> AbrirTurnoAsync(Guid usuarioId, decimal saldoInicial, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            if (await ObterTurnoAbertoAsync(usuarioId, ct) is not null)
                throw new TurnoInvalidoException("Já existe um turno aberto para este usuário.");

            var id = Guid.NewGuid();
            var dataAbertura = DateTime.UtcNow;

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO caixa_turnos (id, tenant_id, usuario_id, data_abertura, saldo_inicial, saldo_fechamento_sistema, saldo_fechamento_informado, status, created_at)
                    VALUES (@Id, @TenantId, @UsuarioId, @DataAbertura, @SaldoInicial, 0, 0, 'ABERTO', @DataAbertura)
                    """,
                    new { Id = id, TenantId = tenantId, UsuarioId = usuarioId, DataAbertura = dataAbertura, SaldoInicial = saldoInicial },
                    cancellationToken: ct));
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                // Corrida de cliques: o índice único parcial (migration 0002) barrou a segunda abertura.
                throw new TurnoInvalidoException("Já existe um turno aberto para este usuário.");
            }

            return new TurnoAtualDto(id, dataAbertura, saldoInicial, "ABERTO");
        }

        public async Task<ResultadoFechamentoTurnoDto> FecharTurnoAsync(Guid turnoId, decimal saldoInformado, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connectionFactory.SetTenantContextAsync(connection, transaction, tenantId, ct);

            var turno = await connection.QuerySingleOrDefaultAsync<TurnoParaFechar>(
                new CommandDefinition(
                    "SELECT saldo_inicial AS SaldoInicial, status AS Status FROM caixa_turnos WHERE tenant_id = @TenantId AND id = @TurnoId FOR UPDATE",
                    new { TenantId = tenantId, TurnoId = turnoId }, transaction, cancellationToken: ct));

            if (turno is null)
                throw new TurnoInvalidoException($"Turno {turnoId} não encontrado.");
            if (turno.Status != "ABERTO")
                throw new TurnoInvalidoException("Este turno já está fechado.");

            var totais = await connection.QuerySingleAsync<TotaisLedger>(
                new CommandDefinition(
                    """
                    SELECT
                        COALESCE(SUM(valor) FILTER (WHERE tipo_operacao = 'CREDITO'), 0) AS Creditos,
                        COALESCE(SUM(valor) FILTER (WHERE tipo_operacao = 'DEBITO'), 0) AS Debitos
                    FROM caixa_ledger
                    WHERE tenant_id = @TenantId AND caixa_id = @TurnoId
                    """,
                    new { TenantId = tenantId, TurnoId = turnoId }, transaction, cancellationToken: ct));

            var saldoSistema = turno.SaldoInicial + totais.Creditos - totais.Debitos;

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE caixa_turnos
                SET status = 'FECHADO', data_fechamento = now(), saldo_fechamento_sistema = @SaldoSistema, saldo_fechamento_informado = @SaldoInformado
                WHERE tenant_id = @TenantId AND id = @TurnoId
                """,
                new { SaldoSistema = saldoSistema, SaldoInformado = saldoInformado, TenantId = tenantId, TurnoId = turnoId },
                transaction, cancellationToken: ct));

            var integros = await LedgerOperacoes.VerificarCaixaAsync(connection, transaction, tenantId, turnoId, ct);

            await transaction.CommitAsync(ct);

            return new ResultadoFechamentoTurnoDto(turnoId, turno.SaldoInicial, totais.Creditos, totais.Debitos, saldoSistema, saldoInformado, saldoInformado - saldoSistema, integros);
        }

        // Dinheiro que deveria estar na gaveta: fundo de troco + créditos - débitos do ledger.
        public async Task<decimal> ObterSaldoDinheiroAsync(Guid turnoId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleAsync<decimal>(new CommandDefinition(
                """
                SELECT t.saldo_inicial + COALESCE((
                    SELECT SUM(CASE WHEN l.tipo_operacao = 'CREDITO' THEN l.valor ELSE -l.valor END)
                    FROM caixa_ledger l WHERE l.tenant_id = t.tenant_id AND l.caixa_id = t.id), 0)
                FROM caixa_turnos t
                WHERE t.tenant_id = @TenantId AND t.id = @TurnoId
                """,
                new { TenantId = tenantId, TurnoId = turnoId }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<LancamentoTurnoDto>> ListarLancamentosAsync(Guid turnoId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var resultado = await connection.QueryAsync<LancamentoTurnoDto>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, valor AS Valor, tipo_operacao AS TipoOperacao, motivo AS Motivo, criado_em AS CriadoEm
                    FROM caixa_ledger
                    WHERE tenant_id = @TenantId AND caixa_id = @TurnoId
                    ORDER BY criado_em DESC
                    """,
                    new { TenantId = tenantId, TurnoId = turnoId },
                    cancellationToken: ct));

            return resultado.AsList();
        }

        private sealed record TurnoParaFechar(decimal SaldoInicial, string Status);

        private sealed record TotaisLedger(decimal Creditos, decimal Debitos);
    }
}
