using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmporioGege.Application.DTOs;
using EmporioGege.Core.Interfaces;

namespace EmporioGege.Infrastructure.Faturamento
{
    public class AsaasClient(HttpClient httpClient, ILogger<AsaasClient> logger) : IAsaasClient
    {
        private static readonly JsonSerializerOptions SerializacaoOpcoes = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async Task<CriarClienteAsaasResultado> CriarClienteAsync(CriarClienteAsaasDto dto, CancellationToken ct = default)
        {
            var corpo = new
            {
                name = dto.Nome,
                cpfCnpj = dto.CpfCnpj is not null ? SomenteDigitos(dto.CpfCnpj) : null,
                email = dto.Email,
                mobilePhone = dto.Telefone
            };

            using var conteudoRequisicao = new StringContent(JsonSerializer.Serialize(corpo, SerializacaoOpcoes), Encoding.UTF8, "application/json");
            using var resposta = await httpClient.PostAsync("customers", conteudoRequisicao, ct);
            var conteudo = await resposta.Content.ReadAsStringAsync(ct);

            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogWarning("Asaas recusou a criação do cliente: {Status} {Conteudo}", resposta.StatusCode, conteudo);
                return new CriarClienteAsaasResultado(false, null,
                    ExtrairMensagemErro(conteudo) ?? $"Asaas recusou a criação do cliente (HTTP {(int)resposta.StatusCode}).");
            }

            using var documento = JsonDocument.Parse(conteudo);
            var id = documento.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            return new CriarClienteAsaasResultado(true, id, null);
        }

        public async Task<CriarAssinaturaAsaasResultado> CriarAssinaturaAsync(CriarAssinaturaAsaasDto dto, CancellationToken ct = default)
        {
            var corpo = new
            {
                customer = dto.ClienteId,
                billingType = dto.TipoCobranca,
                value = dto.Valor,
                cycle = dto.Ciclo,
                nextDueDate = dto.ProximoVencimento.ToString("yyyy-MM-dd"),
                description = dto.Descricao
            };

            using var conteudoRequisicao = new StringContent(JsonSerializer.Serialize(corpo, SerializacaoOpcoes), Encoding.UTF8, "application/json");
            using var resposta = await httpClient.PostAsync("subscriptions", conteudoRequisicao, ct);
            var conteudo = await resposta.Content.ReadAsStringAsync(ct);

            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogWarning("Asaas recusou a criação da assinatura: {Status} {Conteudo}", resposta.StatusCode, conteudo);
                return new CriarAssinaturaAsaasResultado(false, null, null,
                    ExtrairMensagemErro(conteudo) ?? $"Asaas recusou a criação da assinatura (HTTP {(int)resposta.StatusCode}).");
            }

            using var documento = JsonDocument.Parse(conteudo);
            var raiz = documento.RootElement;
            var id = raiz.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var status = raiz.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            return new CriarAssinaturaAsaasResultado(true, id, status, null);
        }

        public async Task<string?> ObterLinkCobrancaAsync(string subscriptionId, CancellationToken ct = default)
        {
            using var resposta = await httpClient.GetAsync($"payments?subscription={Uri.EscapeDataString(subscriptionId)}&limit=1", ct);
            var conteudo = await resposta.Content.ReadAsStringAsync(ct);

            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogWarning("Asaas recusou a consulta de cobranças da assinatura {SubscriptionId}: {Status} {Conteudo}",
                    subscriptionId, resposta.StatusCode, conteudo);
                return null;
            }

            using var documento = JsonDocument.Parse(conteudo);
            if (!documento.RootElement.TryGetProperty("data", out var dadosEl) || dadosEl.ValueKind != JsonValueKind.Array || dadosEl.GetArrayLength() == 0)
            {
                logger.LogWarning("Assinatura {SubscriptionId} ainda não tem nenhuma cobrança gerada na Asaas.", subscriptionId);
                return null;
            }

            var primeiraCobranca = dadosEl[0];
            return primeiraCobranca.TryGetProperty("invoiceUrl", out var urlEl) ? urlEl.GetString() : null;
        }

        public async Task<string?> BuscarClienteIdPorCpfCnpjAsync(string cpfCnpj, CancellationToken ct = default)
        {
            using var resposta = await httpClient.GetAsync($"customers?cpfCnpj={SomenteDigitos(cpfCnpj)}&limit=1", ct);
            var conteudo = await resposta.Content.ReadAsStringAsync(ct);

            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogWarning("Asaas recusou a busca de cliente por CPF/CNPJ: {Status} {Conteudo}", resposta.StatusCode, conteudo);
                return null;
            }

            using var documento = JsonDocument.Parse(conteudo);
            return documento.RootElement.TryGetProperty("data", out var dadosEl) && dadosEl.ValueKind == JsonValueKind.Array && dadosEl.GetArrayLength() > 0
                ? dadosEl[0].GetProperty("id").GetString()
                : null;
        }

        public async Task<CriarCobrancaPixResultado> CriarCobrancaPixAsync(CriarCobrancaPixDto dto, CancellationToken ct = default)
        {
            var corpo = new
            {
                customer = dto.ClienteId,
                billingType = "PIX",
                value = dto.Valor,
                dueDate = dto.Vencimento.ToString("yyyy-MM-dd"),
                description = dto.Descricao,
                externalReference = dto.ReferenciaExterna
            };

            using var conteudoRequisicao = new StringContent(JsonSerializer.Serialize(corpo, SerializacaoOpcoes), Encoding.UTF8, "application/json");
            using var resposta = await httpClient.PostAsync("payments", conteudoRequisicao, ct);
            var conteudo = await resposta.Content.ReadAsStringAsync(ct);

            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogWarning("Asaas recusou a criação da cobrança Pix: {Status} {Conteudo}", resposta.StatusCode, conteudo);
                return new CriarCobrancaPixResultado(false, null, null, null, null,
                    ExtrairMensagemErro(conteudo) ?? $"Asaas recusou a criação da cobrança Pix (HTTP {(int)resposta.StatusCode}).");
            }

            string? cobrancaId;
            using (var documento = JsonDocument.Parse(conteudo))
                cobrancaId = documento.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            if (cobrancaId is null)
                return new CriarCobrancaPixResultado(false, null, null, null, null, "Asaas não devolveu o id da cobrança.");

            // O QR Code vem de um endpoint separado - a criação da cobrança não traz o payload do Pix.
            using var respostaQr = await httpClient.GetAsync($"payments/{Uri.EscapeDataString(cobrancaId)}/pixQrCode", ct);
            var conteudoQr = await respostaQr.Content.ReadAsStringAsync(ct);

            if (!respostaQr.IsSuccessStatusCode)
            {
                logger.LogWarning("Asaas recusou o QR Code da cobrança {CobrancaId}: {Status} {Conteudo}", cobrancaId, respostaQr.StatusCode, conteudoQr);
                return new CriarCobrancaPixResultado(false, cobrancaId, null, null, null,
                    ExtrairMensagemErro(conteudoQr) ?? "Cobrança criada, mas a Asaas não devolveu o QR Code do Pix.");
            }

            using var documentoQr = JsonDocument.Parse(conteudoQr);
            var raiz = documentoQr.RootElement;
            var payload = raiz.TryGetProperty("payload", out var payloadEl) ? payloadEl.GetString() : null;
            var imagem = raiz.TryGetProperty("encodedImage", out var imagemEl) ? imagemEl.GetString() : null;
            // expirationDate vem no horário de Brasília e sem fuso ("2026-10-19 23:59:59").
            DateTime? expiraEm = raiz.TryGetProperty("expirationDate", out var expEl)
                && DateTime.TryParse(expEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var exp)
                ? DateTime.SpecifyKind(exp - EmporioGege.Core.Common.FusoHorarioBrasil.Offset, DateTimeKind.Utc)
                : null;

            return new CriarCobrancaPixResultado(true, cobrancaId, payload, imagem, expiraEm, null);
        }

        private static string? ExtrairMensagemErro(string corpoResposta)
        {
            try
            {
                using var documento = JsonDocument.Parse(corpoResposta);
                if (documento.RootElement.TryGetProperty("errors", out var errosEl) && errosEl.GetArrayLength() > 0)
                {
                    var primeiro = errosEl[0];
                    if (primeiro.TryGetProperty("description", out var msgEl))
                        return msgEl.GetString();
                }
            }
            catch (JsonException)
            {
                // Resposta não veio em JSON - sem detalhe extra pra extrair.
            }
            return null;
        }

        private static string SomenteDigitos(string valor) => new([.. valor.Where(char.IsDigit)]);
    }
}
