namespace EmporioGege.Application.DTOs
{
    public record PagamentoFiadoDto(DateTime CriadoEm, decimal Valor, string FormaPagamento, string? UsuarioNome);
}
