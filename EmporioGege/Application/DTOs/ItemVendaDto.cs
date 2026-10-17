using EmporioGege.Core.Enums;

namespace EmporioGege.Application.DTOs
{
    // Quantidade é em "unidades de venda" — 1 caixa quando TipoPreco == Caixa, 1 unidade
    // nos demais casos. O VendaService resolve o fator de conversão e o preço aplicável
    // (de precos_produto, nunca confiando em valor vindo do cliente).
    // PrecoUnitarioFixo: só a comanda preenche, com o preço gravado em comandas_itens quando o item
    // foi lançado (o que o cliente viu). Nunca vem do navegador.
    public record ItemVendaDto(Guid ProdutoId, int Quantidade, TipoPreco TipoPreco = TipoPreco.Balcao, decimal? PrecoUnitarioFixo = null);
}
