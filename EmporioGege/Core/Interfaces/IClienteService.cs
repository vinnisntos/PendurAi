using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    public interface IClienteService
    {
        Task<IReadOnlyList<ClienteDto>> ListarAsync(CancellationToken ct = default);

        Task<ClienteDto?> ObterAsync(Guid id, CancellationToken ct = default);

        Task<Guid> SalvarAsync(SalvarClienteDto dto, CancellationToken ct = default);

        Task RegistrarPagamentoAsync(Guid id, decimal valor, string formaPagamento, Guid usuarioId, CancellationToken ct = default);

        Task<IReadOnlyList<PagamentoFiadoDto>> ListarPagamentosAsync(Guid clienteId, CancellationToken ct = default);
    }
}
