using EmporioGege.Application.DTOs;
using EmporioGege.Application.Services;
using EmporioGege.Infrastructure.PedidosOnline;

namespace EmporioGege.Tests;

public class PedidoOnlineTests
{
    private static readonly Guid Bolo = Guid.NewGuid();

    private static CriarPedidoOnlineDto Pedido(List<ItemPedidoOnlineDto> itens, string cpf = "123.456.789-09", string telefone = "(15) 99999-1234") =>
        new("Maria", telefone, cpf, itens);

    [Fact]
    public void Junta_itens_repetidos_do_mesmo_produto()
    {
        var itens = PedidoOnlineService.Validar(Pedido([new(Bolo, 2), new(Bolo, 1)]));
        Assert.Equal(3, Assert.Single(itens).Quantidade);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PedidoOnlineService.MaxQuantidadePorItem + 1)]
    public void Recusa_quantidade_fora_do_limite(int quantidade) =>
        Assert.Throws<ArgumentException>(() => PedidoOnlineService.Validar(Pedido([new(Bolo, quantidade)])));

    [Fact]
    public void Recusa_pedido_sem_itens() =>
        Assert.Throws<ArgumentException>(() => PedidoOnlineService.Validar(Pedido([])));

    [Theory]
    [InlineData("123")]
    [InlineData("")]
    public void Recusa_cpf_invalido(string cpf) =>
        Assert.Throws<ArgumentException>(() => PedidoOnlineService.Validar(Pedido([new(Bolo, 1)], cpf: cpf)));

    [Fact]
    public void Recusa_telefone_invalido() =>
        Assert.Throws<ArgumentException>(() => PedidoOnlineService.Validar(Pedido([new(Bolo, 1)], telefone: "1234")));

    [Fact]
    public void Le_pedido_do_external_reference()
    {
        var id = Guid.NewGuid();
        Assert.True(AsaasWebhookService.TentarLerPedidoId($"pedido:{id}", out var lido));
        Assert.Equal(id, lido);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pedido:nao-e-guid")]
    [InlineData("assinatura:123")]
    public void Ignora_external_reference_que_nao_e_pedido(string? referencia) =>
        Assert.False(AsaasWebhookService.TentarLerPedidoId(referencia, out _));
}
