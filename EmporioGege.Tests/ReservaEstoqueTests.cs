using EmporioGege.Application.Services;
using EmporioGege.Infrastructure.PedidosOnline;

namespace EmporioGege.Tests;

public class ReservaEstoqueTests
{
    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(10, 3, 7)]
    [InlineData(3, 3, 0)]
    [InlineData(2, 5, 0)]   // venda de balcão não respeita a reserva: físico abaixo do reservado
    [InlineData(-1, 0, 0)]
    [InlineData(5, -2, 5)]
    public void Disponivel_desconta_a_reserva_sem_ficar_negativo(int estoque, int reservado, int esperado) =>
        Assert.Equal(esperado, ReservaEstoque.Disponivel(estoque, reservado));

    [Fact]
    public void Reserva_le_as_chaves_que_o_pedido_grava_no_jsonb()
    {
        // itens é serializado com JsonSerializerDefaults.Web (camelCase) - se o nome mudar, a
        // reserva passa a somar zero em silêncio.
        Assert.Contains("i->>'produtoId'", ReservaEstoque.SqlReservadoPorProduto);
        Assert.Contains("i->>'quantidade'", ReservaEstoque.SqlReservadoPorProduto);
        Assert.Contains("@TenantId", ReservaEstoque.SqlReservadoPorProduto);
    }

    [Theory]
    [InlineData("suspenso", true)]
    [InlineData("cancelado", true)]
    [InlineData(null, true)]
    [InlineData("ativo", false)]
    [InlineData("trial", false)]
    public void Licenca_bloqueia_vendas_pelo_bot(string? status, bool bloqueia) =>
        Assert.Equal(bloqueia, PedidoOnlineService.LicencaBloqueiaVendas(status));
}
