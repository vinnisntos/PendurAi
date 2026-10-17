using EmporioGege.Application.DTOs;

namespace EmporioGege.Core.Interfaces
{
    public interface IEstoqueMovimentacaoService
    {
        // Entrada de mercadoria (compra/reposição) - incrementa estoque_atual E deixa rastro
        // em estoque_movimentacoes, diferente de editar o número direto no cadastro do
        // produto (que só fica registrado como AJUSTE_MANUAL, sem motivo/fornecedor).
        Task RegistrarEntradaAsync(Guid produtoId, int quantidade, string? justificativa, Guid usuarioId, decimal? custoUnitario = null, CancellationToken ct = default);

        // Baixa manual (quebra/descarte/uso como insumo) - só permitida pra produtos com
        // produtos.permite_baixa_manual = true (restrição de negócio: nem todo produto faz
        // sentido nessa operação, ex. balas/doces não viram insumo de drink). motivo precisa
        // ser um de "QUEBRA", "DESCARTE" ou "USO_INSUMO".
        Task RegistrarBaixaManualAsync(Guid produtoId, int quantidade, string motivo, string justificativa, Guid usuarioId, CancellationToken ct = default);

        Task<IReadOnlyList<MovimentacaoEstoqueDto>> ListarHistoricoAsync(
            DateTime inicio, DateTime fimExclusivo, Guid? produtoId, CancellationToken ct = default);
    }
}
