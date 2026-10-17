using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Entities;

namespace EmporioGege.Application.Services
{
    // Análogo ao EstoqueOperacoes: lógica de lançamento no ledger isolada de
    // gerenciamento de transação, para o VendaService poder creditar a venda em
    // dinheiro na MESMA transação que baixa o estoque e grava a venda — tudo
    // atômico, sem depender de uma segunda transação separada que poderia
    // confirmar o estoque e falhar ao creditar o caixa (ou vice-versa).
    internal static class LedgerOperacoes
    {
        public static async Task<CaixaLedgerEntry> AdicionarAsync(
            DbConnection connection, DbTransaction transaction, Guid tenantId, LancamentoLedgerDto dto, CancellationToken ct)
        {
            var hashAnterior = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    """
                    SELECT hash_verificacao FROM caixa_ledger
                    WHERE tenant_id = @TenantId AND caixa_id = @CaixaId
                    ORDER BY criado_em DESC
                    LIMIT 1
                    """,
                    new { TenantId = tenantId, dto.CaixaId },
                    transaction, cancellationToken: ct));

            var entry = new CaixaLedgerEntry
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CaixaId = dto.CaixaId,
                Valor = dto.Valor,
                TipoOperacao = dto.TipoOperacao,
                Motivo = dto.Motivo,
                CriadoEm = TruncarMicrossegundos(DateTime.UtcNow), // o Postgres guarda microssegundos; o hash precisa bater ao reler
                HashAnterior = hashAnterior
            };
            entry.HashVerificacao = CalcularHash(entry);

            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO caixa_ledger (id, tenant_id, caixa_id, valor, tipo_operacao, motivo, criado_em, hash_anterior, hash_verificacao)
                    VALUES (@Id, @TenantId, @CaixaId, @Valor, @TipoOperacao, @Motivo, @CriadoEm, @HashAnterior, @HashVerificacao)
                    """,
                    new
                    {
                        entry.Id,
                        entry.TenantId,
                        entry.CaixaId,
                        entry.Valor,
                        TipoOperacao = entry.TipoOperacao.ToString().ToUpperInvariant(),
                        entry.Motivo,
                        entry.CriadoEm,
                        entry.HashAnterior,
                        entry.HashVerificacao
                    },
                    transaction, cancellationToken: ct));

            return entry;
        }

        private static DateTime TruncarMicrossegundos(DateTime instante) => instante.AddTicks(-(instante.Ticks % 10));

        /// <summary>
        /// Confere o ledger de um caixa: o hash de cada lançamento bate com o conteúdo e o
        /// hash_anterior aponta pra um lançamento que existe. Valor alterado, lançamento apagado
        /// ou inserido por fora quebra a conferência. Lançamentos antigos foram gravados com o
        /// horário em 100ns e relidos em microssegundos, por isso testa os 10 dígitos possíveis.
        /// </summary>
        public static bool CadeiaIntegra(IReadOnlyList<CaixaLedgerEntry> lancamentos)
        {
            var hashes = new HashSet<string>(lancamentos.Select(l => l.HashVerificacao));
            var primeiros = 0;
            foreach (var l in lancamentos)
            {
                if (l.HashAnterior is null)
                    primeiros++;
                else if (!hashes.Contains(l.HashAnterior))
                    return false;

                var confere = Enumerable.Range(0, 10).Any(extra =>
                {
                    var copia = new CaixaLedgerEntry
                    {
                        HashAnterior = l.HashAnterior, TenantId = l.TenantId, CaixaId = l.CaixaId,
                        Valor = l.Valor, TipoOperacao = l.TipoOperacao, CriadoEm = DateTime.SpecifyKind(l.CriadoEm.AddTicks(extra), DateTimeKind.Utc)
                    };
                    return CalcularHash(copia) == l.HashVerificacao;
                });
                if (!confere)
                    return false;
            }
            return primeiros <= 1;
        }

        public static async Task<bool> VerificarCaixaAsync(DbConnection connection, DbTransaction? transaction, Guid tenantId, Guid caixaId, CancellationToken ct)
        {
            var linhas = await connection.QueryAsync<(Guid Id, decimal Valor, string TipoOperacao, DateTime CriadoEm, string? HashAnterior, string HashVerificacao)>(
                new CommandDefinition(
                    """
                    SELECT id, valor, tipo_operacao, criado_em, hash_anterior, hash_verificacao
                    FROM caixa_ledger WHERE tenant_id = @TenantId AND caixa_id = @CaixaId
                    ORDER BY criado_em
                    """,
                    new { TenantId = tenantId, CaixaId = caixaId }, transaction, cancellationToken: ct));

            return CadeiaIntegra(linhas.Select(l => new CaixaLedgerEntry
            {
                Id = l.Id, TenantId = tenantId, CaixaId = caixaId, Valor = l.Valor,
                TipoOperacao = Enum.Parse<Core.Enums.TipoOperacaoLedger>(l.TipoOperacao, ignoreCase: true),
                CriadoEm = DateTime.SpecifyKind(l.CriadoEm, DateTimeKind.Utc), HashAnterior = l.HashAnterior, HashVerificacao = l.HashVerificacao
            }).ToList());
        }

        internal static string CalcularHash(CaixaLedgerEntry entry)
        {
            var payload = string.Join('|',
                entry.HashAnterior ?? string.Empty,
                entry.TenantId,
                entry.CaixaId,
                entry.Valor.ToString("F2"),
                entry.TipoOperacao,
                entry.CriadoEm.ToString("O"));

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
