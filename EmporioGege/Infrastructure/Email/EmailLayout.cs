namespace EmporioGege.Infrastructure.Email
{
    // Moldura visual dos e-mails transacionais (cabeçalho com a marca, cartão branco, rodapé).
    // Tudo em tabela + estilo inline porque Gmail/Outlook ignoram <style> e flexbox; o logo é PNG
    // por URL absoluta porque SVG não aparece na maioria dos clientes de e-mail. Os templates do
    // Supabase Auth em docs/email-templates/supabase usam a mesma moldura - mexeu aqui, mexa lá.
    public static class EmailLayout
    {
        // Botão padrão da marca, pra usar dentro do corpo dos e-mails.
        public static string Botao(string url, string texto) => $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:24px 0;">
              <tr><td style="border-radius:8px;background:#F2A516;">
                <a href="{url}" style="display:inline-block;padding:13px 26px;font-size:15px;font-weight:600;color:#17130A;text-decoration:none;border-radius:8px;">{texto}</a>
              </td></tr>
            </table>
            """;

        public static string Envolver(string assunto, string corpoHtml, string logoUrl) => $"""
            <!doctype html>
            <html lang="pt-BR">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <title>{System.Net.WebUtility.HtmlEncode(assunto)}</title>
            </head>
            <body style="margin:0;padding:0;background:#F4F4F5;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#F4F4F5;">
              <tr><td align="center" style="padding:32px 16px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;">
                  <tr><td style="padding:0 4px 20px;">
                    <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
                      <td style="vertical-align:middle;"><img src="{logoUrl}" width="36" height="36" alt="" style="display:block;border:0;border-radius:9px;"></td>
                      <td style="vertical-align:middle;padding-left:10px;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:20px;font-weight:700;color:#18181B;letter-spacing:-0.3px;">Pendur<span style="color:#B26F00;">Ai</span></td>
                    </tr></table>
                  </td></tr>
                  <tr><td style="background:#FFFFFF;border:1px solid #E4E4E7;border-radius:12px;padding:32px 32px 28px;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.6;color:#3F3F46;">
                    {corpoHtml}
                  </td></tr>
                  <tr><td style="padding:20px 4px 0;font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:12px;line-height:1.5;color:#A1A1AA;">
                    PendurAi - PDV, estoque e fiado pra sua loja.<br>
                    Você recebeu este e-mail porque tem uma conta no PendurAi.
                  </td></tr>
                </table>
              </td></tr>
            </table>
            </body>
            </html>
            """;
    }
}
