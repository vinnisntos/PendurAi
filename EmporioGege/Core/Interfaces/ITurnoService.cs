using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    public interface ITurnoService
    {
        Task<TurnoAtualDto?> ObterTurnoAbertoAsync(Guid usuarioId, CancellationToken ct = default);

        Task<TurnoAtualDto> AbrirTurnoAsync(Guid usuarioId, decimal saldoInicial, CancellationToken ct = default);

        Task<ResultadoFechamentoTurnoDto> FecharTurnoAsync(Guid turnoId, decimal saldoInformado, CancellationToken ct = default);

        Task<IReadOnlyList<LancamentoTurnoDto>> ListarLancamentosAsync(Guid turnoId, CancellationToken ct = default);

        Task<decimal> ObterSaldoDinheiroAsync(Guid turnoId, CancellationToken ct = default);
    }
}
