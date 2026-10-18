namespace EmporioGege.Application.Services
{
    // Reserva de estoque dos pedidos do bot de WhatsApp: a baixa de verdade só acontece quando
    // o Pix cai (VendaService), mas enquanto o cliente não paga aquelas unidades não podem ser
    // vendidas de novo pelo bot nem aparecer como disponíveis no cardápio. A reserva some sozinha
    // quando o pedido sai de AGUARDANDO_PAGAMENTO (pago, expirado ou erro) ou depois de MinutosReserva.
    public static class ReservaEstoque
    {
        // O QR do Asaas vale até o fim do dia, mas bolo de pote não pode ficar preso até meia-noite
        // por quem desistiu: passada a janela o estoque volta pro cardápio. Se o cliente pagar depois
        // disso e o item tiver acabado, o pedido cai em PAGO_SEM_ESTOQUE (a loja resolve na mão).
        public const int MinutosReserva = 60;

        // Subconsulta (produto_id, reservado) - precisa do parâmetro @TenantId.
        public const string SqlReservadoPorProduto = """
            SELECT (i->>'produtoId')::uuid AS produto_id, sum((i->>'quantidade')::int)::int AS reservado
            FROM pedidos_online po
            CROSS JOIN LATERAL jsonb_array_elements(po.itens) i
            WHERE po.tenant_id = @TenantId
              AND po.status = 'AGUARDANDO_PAGAMENTO'
              AND po.criado_em > now() - interval '60 minutes'
              AND (po.pix_expira_em IS NULL OR po.pix_expira_em > now())
            GROUP BY 1
            """;

        // Estoque físico menos o reservado; nunca negativo (venda de balcão não respeita a reserva,
        // então o físico pode ficar abaixo do reservado).
        public static int Disponivel(int estoqueAtual, int reservado) => Math.Max(estoqueAtual - Math.Max(reservado, 0), 0);
    }
}
