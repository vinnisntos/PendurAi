using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    public interface IFuncionarioService
    {
        Task<IReadOnlyList<FuncionarioDto>> ListarAsync(CancellationToken ct = default);

        Task CriarAsync(CriarFuncionarioDto dto, CancellationToken ct = default);

        Task AtualizarAsync(Guid id, string nome, string role, CancellationToken ct = default);

        Task DefinirAtivoAsync(Guid id, bool ativo, CancellationToken ct = default);
    }
}
