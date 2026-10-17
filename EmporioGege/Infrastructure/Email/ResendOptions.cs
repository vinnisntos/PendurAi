namespace EmporioGege.Infrastructure.Email
{
    public class ResendOptions
    {
        // Chave de API da conta Resend (https://resend.com/api-keys) - vem de user-secrets/
        // variável de ambiente, nunca de appsettings.json (mesmo padrão do token da Asaas).
        public string ApiKey { get; set; } = default!;

        // Remetente precisa ser de um domínio verificado na Resend, senão a API recusa o envio.
        public string RemetenteEmail { get; set; } = "pendurai@noreply.vinnisantos.com.br";

        public string RemetenteNome { get; set; } = "PendurAi";

        // PNG do símbolo usado no cabeçalho dos e-mails (precisa ser URL pública absoluta).
        public string LogoUrl { get; set; } = "https://pendurai.vinnisantos.com.br/img/brand/icon-192.png";
    }
}
