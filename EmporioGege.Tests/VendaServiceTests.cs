using EmporioGege.Application.DTOs;
using EmporioGege.Application.Services;
using EmporioGege.Core.Enums;

namespace EmporioGege.Tests;

public class VendaServiceTests
{
    // A validação roda antes de abrir conexão, então dá pra testar sem banco.
    private static readonly VendaService Servico = new(null!, null!);
    private static readonly List<ItemVendaDto> Itens = [new(Guid.NewGuid(), 1)];

    [Theory]
    [InlineData("BITCOIN", TipoOrigemVenda.Balcao)]
    [InlineData("", TipoOrigemVenda.Balcao)]
    [InlineData(null, TipoOrigemVenda.Balcao)]
    [InlineData(null, TipoOrigemVenda.Comanda)]
    public async Task Recusa_forma_de_pagamento_invalida(string? metodo, TipoOrigemVenda origem)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Servico.FinalizarVendaAsync(new FinalizarVendaDto(null, Itens, origem, false, metodo!, null)));
        Assert.StartsWith("Forma de pagamento inválida", ex.Message);
    }

    [Fact]
    public async Task Recusa_venda_sem_itens() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Servico.FinalizarVendaAsync(new FinalizarVendaDto(null, [], TipoOrigemVenda.Balcao, false, "PIX", null)));
}
