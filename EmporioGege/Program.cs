using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Supabase;
using EmporioGege.Application.Services;
using EmporioGege.Core.Enums;
using EmporioGege.Core.Interfaces;
using EmporioGege.Core.Security;
using EmporioGege.Infrastructure.Auth;
using EmporioGege.Infrastructure.Data;
using EmporioGege.Infrastructure.Email;
using EmporioGege.Infrastructure.Faturamento;
using EmporioGege.Infrastructure.Fiscal;
using EmporioGege.Infrastructure.Impressao;
using EmporioGege.Infrastructure.Relatorios;
using EmporioGege.Infrastructure.Saude;
using EmporioGege.Infrastructure.Tenancy;
using EmporioGege.Infrastructure.Webhooks;

var builder = WebApplication.CreateBuilder(args);

// 0. Dapper não conhece DateOnly como parâmetro nativamente (ver comentário no handler).
Dapper.SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

// QuestPDF exige declarar o tipo de licença em tempo de execução - "Community" é gratuita
// pra empresas com faturamento anual baixo (ver licença deles se o PendurAi crescer muito).
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// 1. Configuração do Supabase (Ajustado com '!' para sumir o Warning CS8604)
var url = builder.Configuration["Supabase:Url"]!;
var key = builder.Configuration["Supabase:Key"]!;

builder.Services.AddScoped<Client>(_ => new Client(url, key, new SupabaseOptions { AutoConnectRealtime = false }));

// 1.1 Acesso direto ao Postgres (Npgsql/Dapper) para operações transacionais
// (ledger do caixa, baixa de estoque) que o cliente REST do Supabase não cobre.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
builder.Services.AddScoped<IEstoqueService, EstoqueService>();
builder.Services.AddScoped<ICaixaLedgerService, CaixaLedgerService>();
builder.Services.AddScoped<ITurnoService, TurnoService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IVendaService, VendaService>();
builder.Services.AddScoped<ICatalogoService, CatalogoService>();
builder.Services.AddScoped<IProdutoService, ProdutoService>();
builder.Services.AddScoped<IEstoqueMovimentacaoService, EstoqueMovimentacaoService>();
builder.Services.AddScoped<IFuncionarioService, FuncionarioService>();
builder.Services.AddScoped<IClienteService, ClienteService>();
builder.Services.AddScoped<IComandaService, ComandaService>();
builder.Services.AddSingleton<SupervisorTentativaLimiter>();
builder.Services.AddSingleton<LoginTentativaLimiter>();
builder.Services.AddScoped<ISupervisorAutorizacaoService, SupervisorAutorizacaoService>();
builder.Services.AddScoped<ITenantService, TenantService>();

// Recibo impresso: impressora térmica ligada por porta serial (COM), configurada por
// máquina física (ver seção "Impressora" em appsettings.json / user-secrets locais) - cada
// PC de caixa tem a sua própria impressora pareada, muitas vezes numa porta diferente.
builder.Services.Configure<ImpressoraOptions>(builder.Configuration.GetSection("Impressora"));
builder.Services.AddScoped<IImpressoraReciboService, SerialPortImpressoraReciboService>();

// Cadastro fiscal (NFC-e via Focus NFe): token da EMPRESA que a Focus NFe devolve fica
// cifrado em repouso (tenants.focus_nfe_token_*_cifrado) - o certificado digital em si
// NUNCA fica com a gente, só passa em memória até a chamada de cadastro (ver
// Application/Services/ConfiguracaoFiscalService.cs).
builder.Services.Configure<CriptografiaOptions>(builder.Configuration.GetSection("Fiscal"));
builder.Services.AddSingleton<CriptografiaSimetrica>();
builder.Services.Configure<FocusNfeOptions>(builder.Configuration.GetSection("FocusNFe"));
builder.Services.AddHttpClient<IFocusNfeClient, FocusNfeClient>((sp, client) =>
{
    var focusNfeOpcoes = sp.GetRequiredService<IOptions<FocusNfeOptions>>().Value;
    client.BaseAddress = new Uri(focusNfeOpcoes.Ambiente == "producao"
        ? "https://api.focusnfe.com.br/"
        : "https://homologacao.focusnfe.com.br/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IConfiguracaoFiscalService, ConfiguracaoFiscalService>();

// Assinatura/cobrança da própria loja pra usar o PendurAi (Asaas) - escopo combinado com o
// usuário 2026-07-21: só automatiza a cobrança de lojas já cadastradas manualmente pelo
// superadmin, não é o cadastro self-service completo (ver Application/Services/
// FaturamentoService.cs e a seção "Assinatura/Cobrança" em SuperAdmin/Adegas/Editar).
// ATUALIZAÇÃO: cadastro self-service público adicionado depois - ver CadastroLojaService,
// que reaproveita este mesmo IFaturamentoService pra criar a assinatura na hora do cadastro.
builder.Services.Configure<AsaasOptions>(builder.Configuration.GetSection("Asaas"));
builder.Services.AddHttpClient<IAsaasClient, AsaasClient>((sp, client) =>
{
    var asaasOpcoes = sp.GetRequiredService<IOptions<AsaasOptions>>().Value;
    client.BaseAddress = new Uri(asaasOpcoes.Ambiente == "producao"
        ? "https://api.asaas.com/v3/"
        : "https://api-sandbox.asaas.com/v3/");
    client.DefaultRequestHeaders.Add("access_token", asaasOpcoes.Token);
    client.DefaultRequestHeaders.Add("User-Agent", "PendurAi"); // Asaas exige User-Agent - HttpClient não define um por padrão
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IFaturamentoService, FaturamentoService>();
builder.Services.AddScoped<IAsaasWebhookService, AsaasWebhookService>();

// E-mail transacional próprio (Resend) - boas-vindas do trial, instruções de pagamento e
// confirmação de pagamento (ver IBillingNotificationService/BillingNotificationService).
// Falha de envio é sempre best-effort (só loga), nunca derruba quem chamou.
builder.Services.Configure<ResendOptions>(builder.Configuration.GetSection("Resend"));
builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>((sp, client) =>
{
    var resendOpcoes = sp.GetRequiredService<IOptions<ResendOptions>>().Value;
    client.BaseAddress = new Uri("https://api.resend.com/");
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resendOpcoes.ApiKey);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IBillingNotificationService, BillingNotificationService>();

// Trial gratuito de 7 dias no autocadastro: converte automaticamente em cobrança Asaas quando
// vence sem o lojista ter clicado em "Assinar agora" (ver CadastroLojaService.DiasTrial).
builder.Services.AddHostedService<TrialExpiradoProcessor>();

// Cadastro self-service público (Pages/CadastroLoja) - cria tenant + login administrador em
// trial gratuito numa única requisição, sem nenhum superadmin envolvido. A assinatura Asaas
// só é criada depois (ver IFaturamentoService.IniciarCobrancaTrialAsync).
builder.Services.AddSingleton<CadastroLojaTentativaLimiter>();
builder.Services.AddScoped<ICadastroLojaService, CadastroLojaService>();

builder.Services.AddSingleton<IRelatorioExportService, RelatorioExportService>();

// 1.2 Permite validar o antiforgery token via header em chamadas AJAX/fetch (PDV),
// já que essas requisições não enviam o campo de formulário __RequestVerificationToken.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

// 1.3 Webhooks de integração (Zé Delivery): fila em memória + processamento assíncrono
// em BackgroundService. AmbientTenantContext permite esse worker (sem HttpContext/Claims)
// declarar o tenant corrente por evento processado.
builder.Services.AddSingleton<AmbientTenantContext>();
builder.Services.AddSingleton<IWebhookQueue, InMemoryWebhookQueue>();
builder.Services.AddScoped<IWebhookRepository, WebhookRepository>();
builder.Services.AddScoped<IWebhookIngestaoService, WebhookIngestaoService>();
builder.Services.AddHostedService<ZeDeliveryWebhookProcessor>();

// Rate limit por token de webhook (não por IP — parceiros costumam disparar de vários IPs/CDNs).
// Protege contra flood/DoS no endpoint público sem exigir autenticação de sessão.
//
// O limite por token sozinho é inútil contra um chamador anônimo: como o token vem da própria
// URL (rota controlada por quem chama), mandar um token diferente a cada request cria uma
// partição nova do FixedWindowRateLimiter toda vez, e o limite nunca acumula. Por isso ele é
// combinado (CreateChained = as duas regras precisam permitir) com um limite GLOBAL do endpoint,
// que não depende de nada que o chamador controle - nenhum volume de tokens distintos escapa dele.
var webhookPorToken = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
{
    var token = httpContext.Request.RouteValues["token"]?.ToString() ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
    return RateLimitPartition.GetFixedWindowLimiter(token, _ => new FixedWindowRateLimiterOptions
    {
        Window = TimeSpan.FromSeconds(10),
        PermitLimit = 30,
        QueueLimit = 0
    });
});

var webhookGlobal = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
    RateLimitPartition.GetFixedWindowLimiter("zedelivery-global", _ => new FixedWindowRateLimiterOptions
    {
        // Teto agregado do endpoint inteiro - existe só pra impedir que trocar de token vire uma
        // forma de nunca ser limitado. Testado ao vivo com 320 requests concorrentes usando tokens
        // distintos: um teto de 300/10s deixava passar gente demais pro pool de conexão do Supabase
        // (só 15 conexões no pooler em modo sessão), derrubando o app inteiro (500 em cascata pra
        // TODOS os tenants, não só o webhook) antes mesmo do rate limiter barrar alguém. 60/10s
        // ainda é bem mais que o tráfego normal de um parceiro sozinho (30/10s já é o teto por
        // token), mas protege o pool compartilhado de verdade.
        Window = TimeSpan.FromSeconds(10),
        PermitLimit = 60,
        QueueLimit = 0
    }));

// RateLimiterOptions.AddPolicy só aceita um Func<HttpContext, RateLimitPartition<TKey>> - não dá
// pra registrar um PartitionedRateLimiter<HttpContext> já pronto (como o CreateChained abaixo) por
// esse caminho. Por isso o limite combinado é aplicado direto no endpoint via AddEndpointFilter
// (ver mapeamento da rota), em vez de builder.Services.AddRateLimiter/app.UseRateLimiter.
var webhookRateLimiter = PartitionedRateLimiter.CreateChained(webhookPorToken, webhookGlobal);

// 2. Ativa a Autenticação por Cookies no navegador
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";         // Se tentar acessar algo trancado deslogado, vai para cá
        options.AccessDeniedPath = "/Auth/Login";   // Se tentar acessar algo que não tem permissão, volta para cá
        options.ExpireTimeSpan = TimeSpan.FromHours(8); // Cookie dura 1 turno de trabalho (8 horas)
        // Atrás do Nginx a requisição chega em HTTP; sem Always o cookie de sessão saía sem a flag Secure.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    });

// O app roda atrás do Nginx na VPS: sem isso o IP de todo mundo vira 127.0.0.1 (o rate limit de
// login por IP passava a valer pra plataforma inteira) e o HSTS nunca era enviado. Por padrão só
// confia em proxy de loopback, então um X-Forwarded-For vindo direto da internet é ignorado.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Chaves do cookie/antiforgery persistidas fora da pasta do deploy: se ficarem num diretório
// efêmero, cada push na main desloga todos os caixas no meio do turno. Caminho via
// DataProtection:KeysPath (ex.: /var/lib/pendurai/keys); sem ele, usa o padrão do SO.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("PendurAi");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddHealthChecks().AddCheck<BancoHealthCheck>("banco");

// 3. Define as Políticas de Segurança baseado nas Roles do banco
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("superadmin"));
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("administrador", "superadmin"));
    options.AddPolicy("CaixaOnly", policy => policy.RequireRole("vendedor", "administrador", "superadmin"));
});

// 4. Registra o Razor Pages e tranca as pastas específicas do sistema
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/SuperAdmin", "SuperAdminOnly");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Caixa", "CaixaOnly");

    // Superadmin pode acessar essas pastas sem ter "entrado" numa loja (SuperAdmin/Adegas) -
    // sem este filtro, qualquer página aqui dentro derrubava com erro 500 genérico ao tentar
    // resolver o tenant (ver ExigeTenantPageFilter).
    options.Conventions.AddFolderApplicationModelConvention("/Admin", model => model.Filters.Add(new TypeFilterAttribute(typeof(ExigeTenantPageFilter))));
    options.Conventions.AddFolderApplicationModelConvention("/Caixa", model => model.Filters.Add(new TypeFilterAttribute(typeof(ExigeTenantPageFilter))));
});

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // shortcut: CSP sem script-src porque as telas ainda têm scripts inline; migrar para nonce antes de endurecer.
    headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
    await next();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// ATENÇÃO: sem isso, o binding de decimal em formulários (preço, limite de crédito, saldo)
// usa a cultura pt-BR da máquina, que lê "." como separador de MILHAR — um <input type=number>
// sempre envia "20.00" (ponto decimal, padrão HTML5, independente do idioma do navegador),
// e sob pt-BR isso vira 2000. Confirmado ao vivo: um limite de crédito digitado como 20.00
// foi salvo como 2000.00. Fixamos a cultura de parsing em invariant; exibição de moeda (R$,
// vírgula decimal) continua controlada explicitamente com CultureInfo pt-BR em cada view.
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture),
    SupportedCultures = [CultureInfo.InvariantCulture],
    SupportedUICultures = [CultureInfo.InvariantCulture]
});

app.UseHttpsRedirection();
app.UseRouting();

// 5. ATENÇÃO: A ordem aqui importa muito!
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/healthz");
app.MapRazorPages().WithStaticAssets();

// 6. Webhook do Zé Delivery — endpoint público (sem cookie de sessão), autenticado por
// token de URL + assinatura HMAC do corpo. Minimal API porque não é uma página navegável.
app.MapPost("/webhooks/zedelivery/{token}", async (
    string token,
    HttpRequest request,
    IWebhookIngestaoService ingestao,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        // Lê o corpo bruto ANTES de qualquer parse/model binding — a assinatura HMAC é
        // calculada sobre os bytes originais recebidos, não sobre uma reserialização.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var corpoBruto = buffer.ToArray();

        // Nome do header de assinatura AINDA NÃO CONFIRMADO com a doc de parceiro da Zé
        // Delivery — ajustar assim que tiver acesso real (ver comentário em HmacSignatureValidator).
        var assinatura = request.Headers["X-Ze-Signature"].FirstOrDefault();

        var resultado = await ingestao.ReceberAsync(CanalIntegracao.ZeDelivery, token, corpoBruto, assinatura, ct);

        return resultado switch
        {
            ResultadoIngestaoWebhook.TokenInvalido => Results.NotFound(),
            ResultadoIngestaoWebhook.AssinaturaInvalida => Results.Unauthorized(),
            ResultadoIngestaoWebhook.PayloadInvalido => Results.BadRequest(),
            ResultadoIngestaoWebhook.Duplicado => Results.Ok(new { status = "ja_processado" }),
            ResultadoIngestaoWebhook.Aceito => Results.Accepted(),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
    catch (Exception ex)
    {
        // Endpoint público e sem sessão: nunca deixar uma exceção não tratada vazar
        // stack trace/caminho de arquivo pra fora (visto isso acontecer ao testar sem
        // banco acessível — o handler global só cobre Razor Pages, não Minimal APIs).
        logger.LogError(ex, "Erro inesperado no webhook Zé Delivery.");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
})
.AddEndpointFilter(async (context, next) =>
{
    using var lease = webhookRateLimiter.AttemptAcquire(context.HttpContext);
    if (!lease.IsAcquired)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    return await next(context);
})
.WithName("ZeDeliveryWebhook");

// 7. Webhook do Asaas — cobrança/assinatura da PRÓPRIA loja pra usar o PendurAi (não é o
// PDV, que é cobrança do cliente final). Endpoint único/global (não por tenant, diferente
// do Zé Delivery) porque há uma só conta Asaas gerenciando a assinatura de todas as lojas;
// autenticado por token fixo no header "asaas-access-token" (configurado no cadastro do
// webhook no painel/API do Asaas, não é HMAC - ver Core/Security/TokenFixoValidator).
app.MapPost("/webhooks/asaas", async (
    HttpRequest request,
    IOptions<AsaasOptions> asaasOpcoes,
    IAsaasWebhookService webhookService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var tokenRecebido = request.Headers["asaas-access-token"].FirstOrDefault();
    if (!TokenFixoValidator.IsValid(tokenRecebido, asaasOpcoes.Value.WebhookToken))
        return Results.Unauthorized();

    try
    {
        using var leitor = new StreamReader(request.Body);
        var corpo = await leitor.ReadToEndAsync(ct);
        await webhookService.ProcessarAsync(corpo, ct);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        // Nunca deixar uma exceção não tratada vazar stack trace pra fora - devolver 500 (em
        // vez de engolir o erro) faz a Asaas reentregar o evento depois, já que aqui não
        // existe fila durável própria (ver AsaasWebhookService: aplicar o mesmo status
        // duas vezes é inofensivo, então isso é seguro).
        logger.LogError(ex, "Erro inesperado no webhook do Asaas.");
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
})
.WithName("AsaasWebhook");

app.Run();