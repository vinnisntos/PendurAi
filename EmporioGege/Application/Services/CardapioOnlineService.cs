using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;
using Npgsql;

namespace EmporioGege.Application.Services
{
    public partial class CardapioOnlineService(IDbConnectionFactory connectionFactory, ITenantProvider tenantProvider) : ICardapioOnlineService
    {
        public const int TamanhoMaximoFoto = 2 * 1024 * 1024;
        public const int TamanhoMaximoDescricao = 300;

        public async Task<CardapioProdutoDto?> ObterProdutoAsync(Guid produtoId, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<CardapioProdutoDto>(new CommandDefinition(
                "SELECT exibir_cardapio AS ExibirCardapio, descricao AS Descricao, foto_url AS FotoUrl FROM produtos WHERE id = @Id AND tenant_id = @TenantId",
                new { Id = produtoId, TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task SalvarProdutoAsync(Guid produtoId, bool exibirCardapio, string? descricao, byte[]? foto, bool removerFoto, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            descricao = string.IsNullOrWhiteSpace(descricao) ? null : descricao.Trim();
            if (descricao?.Length > TamanhoMaximoDescricao)
                throw new ArgumentException($"A descrição pode ter no máximo {TamanhoMaximoDescricao} caracteres.");

            string? tipoFoto = null;
            if (foto is not null && !removerFoto)
            {
                if (foto.Length > TamanhoMaximoFoto)
                    throw new ArgumentException("A foto pode ter no máximo 2 MB.");
                // Pelo conteúdo, não pela extensão/Content-Type que o navegador manda.
                tipoFoto = DetectarTipoImagem(foto) ?? throw new ArgumentException("Envie uma foto em JPG, PNG ou WEBP.");
            }

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var linhas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE produtos SET exibir_cardapio = @Exibir, descricao = @Descricao WHERE id = @Id AND tenant_id = @TenantId",
                new { Exibir = exibirCardapio, Descricao = descricao, Id = produtoId, TenantId = tenantId }, transaction, cancellationToken: ct));
            if (linhas == 0)
                throw new InvalidOperationException("Produto não encontrado.");

            if (removerFoto)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM produtos_fotos WHERE produto_id = @Id AND tenant_id = @TenantId; UPDATE produtos SET foto_url = NULL WHERE id = @Id AND tenant_id = @TenantId",
                    new { Id = produtoId, TenantId = tenantId }, transaction, cancellationToken: ct));
            }
            else if (tipoFoto is not null)
            {
                // ?v= muda a cada troca de foto: a URL antiga pode ficar em cache por 1 dia.
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO produtos_fotos (produto_id, tenant_id, conteudo, content_type, atualizado_em)
                    VALUES (@Id, @TenantId, @Conteudo, @Tipo, now())
                    ON CONFLICT (produto_id) DO UPDATE SET conteudo = excluded.conteudo, content_type = excluded.content_type, atualizado_em = now();
                    UPDATE produtos SET foto_url = '/cardapio/foto/' || @Id::text || '?v=' || @Versao WHERE id = @Id AND tenant_id = @TenantId
                    """,
                    new { Id = produtoId, TenantId = tenantId, Conteudo = foto, Tipo = tipoFoto, Versao = DateTime.UtcNow.Ticks.ToString() },
                    transaction, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
        }

        public async Task<ConfiguracaoCardapioDto> ObterConfiguracaoAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var loja = await connection.QuerySingleAsync<(string? Slug, string? WhatsApp)>(new CommandDefinition(
                "SELECT cardapio_slug, cardapio_whatsapp FROM tenants WHERE id = @TenantId", new { TenantId = tenantId }, cancellationToken: ct));
            var integracao = await connection.QuerySingleOrDefaultAsync<IntegracaoRow>(new CommandDefinition(
                "SELECT token_url AS Token, callback_url AS CallbackUrl FROM integracoes_webhook WHERE tenant_id = @TenantId AND canal = 'WHATSAPP' AND ativo = true",
                new { TenantId = tenantId }, cancellationToken: ct));

            return new ConfiguracaoCardapioDto(loja.Slug, loja.WhatsApp, integracao is not null,
                integracao?.Token[^4..], integracao?.CallbackUrl);
        }

        public async Task SalvarConfiguracaoAsync(string? slug, string? whatsApp, string? callbackUrl, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            slug = NormalizarSlug(slug);
            whatsApp = NormalizarWhatsApp(whatsApp);
            callbackUrl = string.IsNullOrWhiteSpace(callbackUrl) ? null : callbackUrl.Trim();
            if (callbackUrl is not null && !(Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
                throw new ArgumentException("O endereço do bot precisa começar com https://");

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE tenants SET cardapio_slug = @Slug, cardapio_whatsapp = @WhatsApp WHERE id = @TenantId",
                    new { Slug = slug, WhatsApp = whatsApp, TenantId = tenantId }, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                throw new ArgumentException("Esse endereço de cardápio já está em uso por outra loja.");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE integracoes_webhook SET callback_url = @CallbackUrl WHERE tenant_id = @TenantId AND canal = 'WHATSAPP'",
                new { CallbackUrl = callbackUrl, TenantId = tenantId }, cancellationToken: ct));
        }

        public async Task<CredenciaisBotDto> GerarCredenciaisAsync(CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            var credenciais = new CredenciaisBotDto(
                Base64UrlToken(RandomNumberGenerator.GetBytes(32)),
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO integracoes_webhook (tenant_id, canal, token_url, segredo_hmac, ativo)
                VALUES (@TenantId, 'WHATSAPP', @Token, @Segredo, true)
                ON CONFLICT (tenant_id, canal) DO UPDATE SET token_url = excluded.token_url, segredo_hmac = excluded.segredo_hmac, ativo = true
                """,
                new { TenantId = tenantId, credenciais.Token, credenciais.Segredo }, cancellationToken: ct));

            return credenciais;
        }

        public async Task<IReadOnlyList<PedidoOnlineResumoDto>> ListarPedidosAsync(int dias, CancellationToken ct = default)
        {
            var tenantId = tenantProvider.RequireTenantId();
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var pedidos = await connection.QueryAsync<PedidoOnlineResumoDto>(new CommandDefinition(
                """
                SELECT id AS Id, criado_em AS CriadoEm, cliente_nome AS ClienteNome, cliente_telefone AS ClienteTelefone,
                       total AS Total, status AS Status, erro AS Erro
                FROM pedidos_online
                WHERE tenant_id = @TenantId AND criado_em >= now() - make_interval(days => @Dias)
                ORDER BY criado_em DESC LIMIT 200
                """,
                new { TenantId = tenantId, Dias = dias }, cancellationToken: ct));
            return pedidos.AsList();
        }

        public async Task<CardapioPublicoDto?> ObterCardapioPublicoAsync(string slug, CancellationToken ct = default)
        {
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            var loja = await connection.QuerySingleOrDefaultAsync<LojaRow>(new CommandDefinition(
                """
                SELECT id AS Id, coalesce(nome_fantasia, 'Cardápio') AS Nome, cardapio_whatsapp AS WhatsApp FROM tenants
                WHERE cardapio_slug = @Slug AND status_licenca NOT IN ('suspenso', 'cancelado')
                """,
                new { Slug = slug.ToLowerInvariant() }, cancellationToken: ct));
            if (loja is null)
                return null;

            // Esgotado considera o que já está reservado por pedidos aguardando Pix (ver ReservaEstoque).
            var itens = await connection.QueryAsync<ItemCardapioPublicoDto>(new CommandDefinition(
                $"""
                SELECT p.id AS Id, p.nome AS Nome, p.descricao AS Descricao, p.foto_url AS FotoUrl, p.preco_venda_base AS Preco,
                       p.estoque_atual - coalesce(r.reservado, 0) <= 0 AS Esgotado
                FROM produtos p
                LEFT JOIN ({ReservaEstoque.SqlReservadoPorProduto}) r ON r.produto_id = p.id
                WHERE p.tenant_id = @TenantId AND p.ativo = true AND p.exibir_cardapio = true
                ORDER BY p.estoque_atual - coalesce(r.reservado, 0) <= 0, p.nome
                """,
                new { TenantId = loja.Id }, cancellationToken: ct));

            return new CardapioPublicoDto(loja.Nome, loja.WhatsApp, itens.AsList());
        }

        public async Task<FotoProdutoDto?> ObterFotoPublicaAsync(Guid produtoId, CancellationToken ct = default)
        {
            // Só foto de produto que está no cardápio: as demais não são públicas.
            await using var connection = await connectionFactory.CreateOpenConnectionAsync(ct);
            return await connection.QuerySingleOrDefaultAsync<FotoProdutoDto>(new CommandDefinition(
                """
                SELECT f.conteudo AS Conteudo, f.content_type AS ContentType, f.atualizado_em AS AtualizadoEm
                FROM produtos_fotos f JOIN produtos p ON p.id = f.produto_id AND p.tenant_id = f.tenant_id
                WHERE f.produto_id = @Id AND p.exibir_cardapio = true AND p.ativo = true
                """,
                new { Id = produtoId }, cancellationToken: ct));
        }

        public static string? DetectarTipoImagem(ReadOnlySpan<byte> conteudo)
        {
            if (conteudo.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
                return "image/jpeg";
            if (conteudo.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
                return "image/png";
            if (conteudo.Length >= 12 && conteudo[..4].SequenceEqual("RIFF"u8) && conteudo[8..12].SequenceEqual("WEBP"u8))
                return "image/webp";
            return null;
        }

        public static string? NormalizarSlug(string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return null;
            var normalizado = slug.Trim().ToLowerInvariant().Replace(' ', '-');
            if (!SlugValido().IsMatch(normalizado))
                throw new ArgumentException("O endereço do cardápio deve ter de 3 a 40 letras minúsculas, números ou hífens (ex.: bolos-da-gabi).");
            return normalizado;
        }

        // Guarda no formato do wa.me: só dígitos, com o 55 do Brasil.
        public static string? NormalizarWhatsApp(string? numero)
        {
            if (string.IsNullOrWhiteSpace(numero))
                return null;
            var digitos = new string([.. numero.Where(char.IsDigit)]);
            if (digitos.Length is 10 or 11)
                digitos = "55" + digitos;
            if (digitos.Length is not (12 or 13) || !digitos.StartsWith("55"))
                throw new ArgumentException("Informe o WhatsApp com DDD, ex.: (15) 99999-1234.");
            return digitos;
        }

        private static string Base64UrlToken(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed record IntegracaoRow(string Token, string? CallbackUrl);

        private sealed record LojaRow(Guid Id, string Nome, string? WhatsApp);

        [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$")]
        private static partial Regex SlugValido();
    }
}
