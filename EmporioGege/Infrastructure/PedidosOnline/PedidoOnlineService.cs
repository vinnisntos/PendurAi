using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Application.Services;
using EmporioGege.Core.Common;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Exceptions;
using EmporioGege.Core.Interfaces;
using EmporioGege.Infrastructure.Tenancy;
using Npgsql;

namespace EmporioGege.Infrastructure.PedidosOnline
{
    // Fica em Infrastructure (não em Application) porque a confirmação de pagamento chega pelo
    // webhook do Asaas, sem sessão: precisa declarar o tenant do pedido via AmbientTenantContext
    // pra reaproveitar o VendaService (baixa de estoque + venda na mesma transação).
    public class PedidoOnlineService(
        IDbConnectionFactory connectionFactory,
        ITenantProvider tenantProvider,
        AmbientTenantContext ambientTenantContext,
        IAsaasClient asaasClient,
        IVendaService vendaService,
        IHttpClientFactory httpClientFactory,
        ILogger<PedidoOnlineService> logger) : IPedidoOnlineService
    {
        public const string PrefixoReferencia = "pedido:";
        public const int MaxItens = 20;
        public const int MaxQuantidadePorItem = 50;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public async Task<IReadOnlyList<ItemCardapioDto>> ListarCardapioAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            // Disponível = físico menos o que está reservado por pedidos aguardando Pix.
            var itens = await connection.QueryAsync<ItemCardapioDto>(new CommandDefinition(
                $"""
                SELECT p.id AS Id, p.nome AS Nome, p.descricao AS Descricao, p.foto_url AS FotoUrl,
                       p.preco_venda_base AS Preco, greatest(p.estoque_atual - coalesce(r.reservado, 0), 0) AS EstoqueDisponivel
                FROM produtos p
                LEFT JOIN ({ReservaEstoque.SqlReservadoPorProduto}) r ON r.produto_id = p.id
                WHERE p.tenant_id = @TenantId AND p.ativo = true AND p.exibir_cardapio = true
                ORDER BY p.nome
                """,
                new { TenantId = tenantId }, cancellationToken: ct));

            return itens.AsList();
        }

        public async Task<PedidoOnlineDto> CriarPedidoAsync(CriarPedidoOnlineDto dto, CancellationToken ct = default)
        {
            var itensPedido = Validar(dto);
            var tenantId = tenantProvider.RequireTenantId();
            var pedidoId = Guid.NewGuid();

            List<ItemPedidoGravadoDto> itensGravados;
            decimal total;
            string nomeLoja;

            await using (var connection = await connectionFactory.CreateOpenConnectionAsync(ct))
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                await connectionFactory.SetTenantContextAsync(connection, transaction, tenantId, ct);
                var ids = itensPedido.Select(i => i.ProdutoId).ToArray();

                // FOR UPDATE (em ordem de id, contra deadlock) serializa dois pedidos simultâneos dos
                // mesmos produtos: o segundo só lê a reserva depois que o primeiro gravou a dele, e
                // em READ COMMITTED cada comando enxerga o que já foi commitado - sem isso os dois
                // passavam pela checagem e o mesmo último pote era reservado duas vezes.
                // Preço sempre do banco, nunca do bot - o Pix sai com o valor que o PendurAi calculou.
                var produtos = (await connection.QueryAsync<ProdutoCardapioRow>(new CommandDefinition(
                    """
                    SELECT id AS Id, nome AS Nome, preco_venda_base AS Preco, estoque_atual AS Estoque
                    FROM produtos
                    WHERE tenant_id = @TenantId AND id = ANY(@Ids) AND ativo = true AND exibir_cardapio = true
                    ORDER BY id
                    FOR UPDATE
                    """,
                    new { TenantId = tenantId, Ids = ids }, transaction, cancellationToken: ct)))
                    .ToDictionary(p => p.Id);

                var reservado = (await connection.QueryAsync<(Guid ProdutoId, int Reservado)>(new CommandDefinition(
                    $"SELECT produto_id, reservado FROM ({ReservaEstoque.SqlReservadoPorProduto}) r WHERE produto_id = ANY(@Ids)",
                    new { TenantId = tenantId, Ids = ids }, transaction, cancellationToken: ct)))
                    .ToDictionary(r => r.ProdutoId, r => r.Reservado);

                itensGravados = [];
                foreach (var item in itensPedido)
                {
                    if (!produtos.TryGetValue(item.ProdutoId, out var produto))
                        throw new ArgumentException($"Produto {item.ProdutoId} não está disponível no cardápio.");

                    // A baixa de verdade só acontece quando o Pix cair; aqui o pedido reserva as
                    // unidades (ver ReservaEstoque) e não pode passar do que sobra sem reserva.
                    var disponivel = ReservaEstoque.Disponivel(produto.Estoque, reservado.GetValueOrDefault(produto.Id));
                    if (disponivel < item.Quantidade)
                        throw new EstoqueInsuficienteException(item.ProdutoId, item.Quantidade, disponivel);

                    itensGravados.Add(new ItemPedidoGravadoDto(produto.Id, produto.Nome, item.Quantidade, produto.Preco));
                }

                total = itensGravados.Sum(i => i.PrecoUnitario * i.Quantidade);
                if (total <= 0)
                    throw new ArgumentException("O pedido precisa ter valor maior que zero.");

                nomeLoja = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                    "SELECT nome_fantasia FROM tenants WHERE id = @TenantId", new { TenantId = tenantId }, transaction, cancellationToken: ct)) ?? "PendurAi";

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO pedidos_online (id, tenant_id, cliente_nome, cliente_telefone, itens, total, observacao, status)
                    VALUES (@Id, @TenantId, @Nome, @Telefone, @Itens::jsonb, @Total, @Observacao, 'AGUARDANDO_PAGAMENTO')
                    """,
                    new
                    {
                        Id = pedidoId,
                        TenantId = tenantId,
                        Nome = dto.ClienteNome.Trim(),
                        Telefone = SomenteDigitos(dto.ClienteTelefone),
                        Itens = JsonSerializer.Serialize(itensGravados, Json),
                        Total = total,
                        Observacao = string.IsNullOrWhiteSpace(dto.Observacao) ? null : dto.Observacao.Trim()
                    },
                    transaction, cancellationToken: ct));

                // Commit antes de chamar o Asaas: não segura o lock dos produtos durante a chamada HTTP.
                await transaction.CommitAsync(ct);
            }

            var cobranca = await GerarCobrancaAsync(dto, total, pedidoId, nomeLoja, ct);

            await using (var connection = await connectionFactory.CreateOpenConnectionAsync(ct))
            {
                if (!cobranca.Sucesso)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE pedidos_online SET status = 'ERRO', erro = @Erro, asaas_payment_id = @CobrancaId WHERE id = @Id AND tenant_id = @TenantId",
                        new { Erro = cobranca.MensagemErro, cobranca.CobrancaId, Id = pedidoId, TenantId = tenantId }, cancellationToken: ct));
                    throw new InvalidOperationException(cobranca.MensagemErro ?? "Não foi possível gerar o Pix.");
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE pedidos_online SET asaas_payment_id = @CobrancaId, pix_copia_cola = @PixCopiaECola, pix_expira_em = @PixExpiraEm
                    WHERE id = @Id AND tenant_id = @TenantId
                    """,
                    new { cobranca.CobrancaId, cobranca.PixCopiaECola, cobranca.PixExpiraEm, Id = pedidoId, TenantId = tenantId }, cancellationToken: ct));
            }

            return new PedidoOnlineDto(pedidoId, "AGUARDANDO_PAGAMENTO", total, itensGravados,
                cobranca.PixCopiaECola, cobranca.PixQrCodeBase64, cobranca.PixExpiraEm, DateTime.UtcNow, null, null);
        }

        public async Task<PedidoOnlineDto?> ObterPedidoAsync(Guid id, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var row = await BuscarAsync(connection, "id = @Id AND tenant_id = @TenantId", new { Id = id, TenantId = tenantId }, ct);
            return row?.ParaDto();
        }

        public async Task<int> ContarAguardandoPagamentoAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM pedidos_online WHERE tenant_id = @TenantId AND status = 'AGUARDANDO_PAGAMENTO'",
                new { TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task ConfirmarPagamentoAsync(Guid pedidoId, string cobrancaId, CancellationToken ct = default)
        {
            PedidoRow? pedido;
            await using (var connection = await connectionFactory.CreateOpenConnectionAsync(ct))
                pedido = await BuscarAsync(connection, "id = @Id AND asaas_payment_id = @CobrancaId", new { Id = pedidoId, CobrancaId = cobrancaId }, ct);

            if (pedido is null)
            {
                logger.LogWarning("Pagamento Asaas {CobrancaId} referencia o pedido {PedidoId}, que não existe.", cobrancaId, pedidoId);
                return;
            }

            if (pedido.Status is "PAGO" or "PAGO_SEM_ESTOQUE")
                return; // Reentrega do evento (ou PAYMENT_CONFIRMED + PAYMENT_RECEIVED do mesmo Pix).

            using var escopoTenant = ambientTenantContext.BeginScope(pedido.TenantId);

            Guid? vendaId = null;
            string novoStatus = "PAGO";
            string? erro = null;
            try
            {
                var itensVenda = pedido.ItensLidos().Select(i => new ItemVendaDto(i.ProdutoId, i.Quantidade, PrecoUnitarioFixo: i.PrecoUnitario)).ToList();
                var venda = await vendaService.FinalizarVendaAsync(
                    new FinalizarVendaDto(null, itensVenda, TipoOrigemVenda.WhatsApp, false, "PIX", ReferenciaExterna: pedido.Id.ToString()), ct);
                vendaId = venda.VendaId;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "ux_vendas_referencia_externa")
            {
                // Outra entrega do mesmo evento já virou venda; só falta marcar o pedido.
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                vendaId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM vendas WHERE tenant_id = @TenantId AND tipo_origem = 'WHATSAPP' AND referencia_externa = @Ref",
                    new { pedido.TenantId, Ref = pedido.Id.ToString() }, cancellationToken: ct));
            }
            catch (EstoqueInsuficienteException ex)
            {
                // O dinheiro já entrou: não dá pra simplesmente recusar. Fica visível pra loja
                // resolver na mão (produzir mais ou estornar o Pix).
                novoStatus = "PAGO_SEM_ESTOQUE";
                erro = ex.Message;
                logger.LogWarning("Pedido {PedidoId} pago sem estoque suficiente: {Erro}", pedido.Id, ex.Message);
            }

            await using (var connection = await connectionFactory.CreateOpenConnectionAsync(ct))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE pedidos_online SET status = @Status, venda_id = @VendaId, erro = @Erro, pago_em = now()
                    WHERE id = @Id AND tenant_id = @TenantId AND status NOT IN ('PAGO', 'PAGO_SEM_ESTOQUE')
                    """,
                    new { Status = novoStatus, VendaId = vendaId, Erro = erro, pedido.Id, pedido.TenantId }, cancellationToken: ct));

                pedido = await BuscarAsync(connection, "id = @Id", new { pedido.Id }, ct);
            }

            if (pedido is not null)
                await AvisarBotAsync(pedido, ct);
        }

        public async Task ExpirarAsync(Guid pedidoId, string cobrancaId, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE pedidos_online SET status = 'EXPIRADO' WHERE id = @Id AND asaas_payment_id = @CobrancaId AND status = 'AGUARDANDO_PAGAMENTO'",
                new { Id = pedidoId, CobrancaId = cobrancaId }, cancellationToken: ct));
        }

        // Mesmo critério do cardápio público: só suspensa/cancelada bloqueia (trial vende normalmente).
        // Loja inexistente (null) também bloqueia - integração órfã não pode criar pedido.
        public static bool LicencaBloqueiaVendas(string? statusLicenca) => statusLicenca is null or "suspenso" or "cancelado";

        // Junta itens repetidos e valida antes de qualquer acesso a banco/Asaas.
        public static List<ItemPedidoOnlineDto> Validar(CriarPedidoOnlineDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.ClienteNome) || dto.ClienteNome.Trim().Length < 2)
                throw new ArgumentException("Informe o nome do cliente.");

            var telefone = SomenteDigitos(dto.ClienteTelefone ?? "");
            if (telefone.Length is < 10 or > 13)
                throw new ArgumentException("Telefone do cliente inválido.");

            var documento = SomenteDigitos(dto.ClienteCpf ?? "");
            if (documento.Length is not (11 or 14))
                throw new ArgumentException("CPF inválido.");

            if (dto.Itens is null || dto.Itens.Count == 0)
                throw new ArgumentException("O pedido precisa ter ao menos um item.");

            var itens = dto.Itens
                .GroupBy(i => i.ProdutoId)
                .Select(g => new ItemPedidoOnlineDto(g.Key, g.Sum(i => i.Quantidade)))
                .ToList();

            if (itens.Count > MaxItens)
                throw new ArgumentException($"O pedido pode ter no máximo {MaxItens} produtos diferentes.");
            if (itens.Any(i => i.Quantidade is < 1 or > MaxQuantidadePorItem))
                throw new ArgumentException($"Quantidade por produto deve ficar entre 1 e {MaxQuantidadePorItem}.");

            return itens;
        }

        private async Task<CriarCobrancaPixResultado> GerarCobrancaAsync(CriarPedidoOnlineDto dto, decimal total, Guid pedidoId, string nomeLoja, CancellationToken ct)
        {
            var clienteId = await asaasClient.BuscarClienteIdPorCpfCnpjAsync(dto.ClienteCpf, ct);
            if (clienteId is null)
            {
                var cliente = await asaasClient.CriarClienteAsync(
                    new CriarClienteAsaasDto(dto.ClienteNome.Trim(), dto.ClienteCpf, null, SomenteDigitos(dto.ClienteTelefone)), ct);
                if (!cliente.Sucesso || cliente.ClienteId is null)
                    return new CriarCobrancaPixResultado(false, null, null, null, null, cliente.MensagemErro);
                clienteId = cliente.ClienteId;
            }

            return await asaasClient.CriarCobrancaPixAsync(new CriarCobrancaPixDto(
                clienteId, total, $"{nomeLoja} - pedido {pedidoId.ToString()[..8]}", FusoHorarioBrasil.HojeLocal(), PrefixoReferencia + pedidoId), ct);
        }

        // Best-effort: o bot também consegue consultar GET /api/v1/pedidos/{id}, então uma falha
        // aqui só atrasa o aviso ao cliente, não perde o pagamento.
        private async Task AvisarBotAsync(PedidoRow pedido, CancellationToken ct)
        {
            try
            {
                await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
                var integracao = await connection.QuerySingleOrDefaultAsync<(string? CallbackUrl, string Segredo)>(new CommandDefinition(
                    "SELECT callback_url, segredo_hmac FROM integracoes_webhook WHERE tenant_id = @TenantId AND canal = 'WHATSAPP' AND ativo = true",
                    new { pedido.TenantId }, cancellationToken: ct));

                if (string.IsNullOrWhiteSpace(integracao.CallbackUrl))
                    return;

                var corpo = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    evento = pedido.Status == "PAGO" ? "pedido.pago" : "pedido.pago_sem_estoque",
                    clienteTelefone = pedido.ClienteTelefone,
                    clienteNome = pedido.ClienteNome,
                    pedido = pedido.ParaDto()
                }, Json);

                using var requisicao = new HttpRequestMessage(HttpMethod.Post, integracao.CallbackUrl) { Content = new ByteArrayContent(corpo) };
                requisicao.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                requisicao.Headers.Add("X-PendurAi-Signature",
                    "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(integracao.Segredo), corpo)));

                using var resposta = await httpClientFactory.CreateClient("BotCallback").SendAsync(requisicao, ct);
                if (!resposta.IsSuccessStatusCode)
                    logger.LogWarning("Bot respondeu {Status} ao aviso do pedido {PedidoId}.", resposta.StatusCode, pedido.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao avisar o bot sobre o pedido {PedidoId}.", pedido.Id);
            }
        }

        private static async Task<PedidoRow?> BuscarAsync(System.Data.Common.DbConnection connection, string filtro, object parametros, CancellationToken ct) =>
            await connection.QuerySingleOrDefaultAsync<PedidoRow>(new CommandDefinition(
                $"""
                SELECT id AS Id, tenant_id AS TenantId, cliente_nome AS ClienteNome, cliente_telefone AS ClienteTelefone,
                       itens::text AS ItensJson, total AS Total, status AS Status, pix_copia_cola AS PixCopiaECola,
                       pix_expira_em AS PixExpiraEm, criado_em AS CriadoEm, pago_em AS PagoEm, erro AS Erro
                FROM pedidos_online WHERE {filtro}
                """,
                parametros, cancellationToken: ct));

        private static string SomenteDigitos(string valor) => new([.. valor.Where(char.IsDigit)]);

        private sealed record ProdutoCardapioRow(Guid Id, string Nome, decimal Preco, int Estoque);

        private sealed class PedidoRow
        {
            public Guid Id { get; init; }
            public Guid TenantId { get; init; }
            public string ClienteNome { get; init; } = default!;
            public string ClienteTelefone { get; init; } = default!;
            public string ItensJson { get; init; } = "[]";
            public decimal Total { get; init; }
            public string Status { get; init; } = default!;
            public string? PixCopiaECola { get; init; }
            public DateTime? PixExpiraEm { get; init; }
            public DateTime CriadoEm { get; init; }
            public DateTime? PagoEm { get; init; }
            public string? Erro { get; init; }

            public List<ItemPedidoGravadoDto> ItensLidos() => JsonSerializer.Deserialize<List<ItemPedidoGravadoDto>>(ItensJson, Json) ?? [];

            public PedidoOnlineDto ParaDto() =>
                new(Id, Status, Total, ItensLidos(), PixCopiaECola, null, PixExpiraEm, CriadoEm, PagoEm, Erro);
        }
    }
}
