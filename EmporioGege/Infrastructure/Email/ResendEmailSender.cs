using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmporioGege.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace EmporioGege.Infrastructure.Email
{
    // Cliente HTTP da API da Resend (https://resend.com/docs/api-reference/emails/send-email).
    // Falha de envio nunca pode derrubar o fluxo de negócio que a disparou (cadastro, webhook
    // de pagamento) - por isso só loga o erro, nunca propaga exceção (ver todos os call sites
    // em BillingNotificationService).
    public class ResendEmailSender(HttpClient httpClient, IOptions<ResendOptions> opcoes, ILogger<ResendEmailSender> logger) : IEmailSender
    {
        private static readonly JsonSerializerOptions SerializacaoOpcoes = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public async Task EnviarAsync(string destinatarioEmail, string destinatarioNome, string assunto, string corpoHtml, CancellationToken ct = default)
        {
            var remetente = $"{opcoes.Value.RemetenteNome} <{opcoes.Value.RemetenteEmail}>";
            var corpo = new
            {
                from = remetente,
                to = new[] { destinatarioEmail },
                subject = assunto,
                html = EmailLayout.Envolver(assunto, corpoHtml, opcoes.Value.LogoUrl)
            };

            try
            {
                using var conteudoRequisicao = new StringContent(JsonSerializer.Serialize(corpo, SerializacaoOpcoes), Encoding.UTF8, "application/json");
                using var resposta = await httpClient.PostAsync("emails", conteudoRequisicao, ct);

                if (!resposta.IsSuccessStatusCode)
                {
                    var conteudoErro = await resposta.Content.ReadAsStringAsync(ct);
                    logger.LogError("Resend recusou o envio de e-mail para {Destinatario} ({Assunto}): {Status} {Conteudo}",
                        destinatarioEmail, assunto, resposta.StatusCode, conteudoErro);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao chamar a API da Resend enviando e-mail para {Destinatario} ({Assunto}).", destinatarioEmail, assunto);
            }
        }
    }
}
