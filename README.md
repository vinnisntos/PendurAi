# 🍷 PendurAi — Sistema de Gestão para Adegas e Mercados

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-10-239120?logo=csharp&logoColor=white)
![Razor Pages](https://img.shields.io/badge/ASP.NET%20Core-Razor%20Pages-512BD4?logo=dotnet&logoColor=white)
![Supabase](https://img.shields.io/badge/Supabase-Postgres%20%2B%20Auth-3ECF8E?logo=supabase&logoColor=white)
![Bootstrap](https://img.shields.io/badge/Bootstrap-5-7952B3?logo=bootstrap&logoColor=white)
![Status](https://img.shields.io/badge/status-beta-orange)
![Hospedagem](https://img.shields.io/badge/AWS%20EC2-online-success?logo=amazonaws&logoColor=white)
![License](https://img.shields.io/badge/license-proprietary-red)

Sistema completo de **Frente de Caixa (PDV)** e **Retaguarda (ERP)**, multi-tenant, para automatizar a operação de adegas e mercados de pequeno/médio porte — da venda rápida no balcão ao controle detalhado de estoque, fiado, turno de caixa e comandas.

> O repositório se chamava `EmporioGege` por motivo histórico (nome da loja piloto usada no desenvolvimento). O produto comercial é o **PendurAi**, desenhado desde o início para ser vendido a múltiplas lojas, e já está no ar em **[pendurai.vinnisantos.com.br](https://pendurai.vinnisantos.com.br)**.

---

## 📑 Sumário

- [Funcionalidades](#-funcionalidades)
- [Arquitetura](#-arquitetura)
- [Stack Tecnológico](#-stack-tecnológico)
- [Status do Projeto](#-status-do-projeto)
- [Status do Repositório](#-status-do-repositório)

## ✅ Funcionalidades

| Módulo | Descrição |
|---|---|
| 🛒 **PDV (Frente de Caixa)** | Venda por código de barras ou nome, atalhos de teclado (F2/F8/F10), preço por unidade ou fechamento de caixa/fardo, transação atômica (estoque + venda + ledger/fiado em uma única operação). |
| 📋 **Comandas** | Abertura por número/mesa, lançamento incremental de itens, fechamento em venda, cancelamento, e autorização de supervisor para overrides do operador. |
| 💰 **Caixa Cego** | Abertura/fechamento de turno com contagem cega (sem ver o saldo esperado), sangria/suprimento em ledger imutável, detecção automática de quebra de caixa. |
| 📦 **Estoque com Fator de Conversão** | Produto cadastrado na unidade mínima (ex: lata) e vendido em variações (ex: fardo com 12), com abatimento automático e Kardex (auditoria) de todo movimento. |
| 🤝 **Carteira / Fiado** | Limite de crédito por cliente, cadastro com CPF/RG, saldo devedor, extrato detalhado e baixa parcial ou total direto no caixa. |
| 🧾 **Extrato de Vendas** | Listagem por período com detalhe de itens, reimpressão manual de recibo e exportação em Excel/PDF/XML/CSV. |
| 📥 **Entrada de Estoque & Auditoria** | Registro de reposição/compra com justificativa, e histórico completo de toda movimentação (entrada, saída por venda, ajuste manual) com exportação. |
| 🏆 **Produtos Mais Vendidos** | Ranking por quantidade vendida em qualquer período, exportável em Excel/PDF/XML/CSV. |
| 🖨️ **Recibo Impresso** | Impressão térmica automática (ESC/POS) via porta serial/Bluetooth ao fechar venda ou comanda. |
| 📊 **Dashboard Administrativo** | Receita (dia/semana/mês), CMV, lucro bruto, ROI, alertas de estoque mínimo, validade próxima, saldo de fiado e comandas ativas. |
| 🏢 **Multi-tenant & SuperAdmin** | Isolamento de dados por loja (`tenant_id`), painel de superadmin para cadastrar lojas e "entrar" em qualquer uma para suporte/configuração. |
| 🔐 **Acesso & Licenciamento** | Papéis vendedor/administrador/superadmin via Supabase Auth + Claims/Policies; login bloqueado automaticamente para licença suspensa/cancelada/expirada; proteção contra força bruta no login e na senha de supervisor. |
| 💳 **Cobrança Recorrente (Asaas)** | Assinatura e cobrança automatizada por loja via Asaas (produção), com webhook atualizando o status da licença sozinho conforme o pagamento é confirmado, vence ou é cancelado. |
| 🚚 **Integração Zé Delivery** | Recebimento de pedidos via webhook (fila durável + background service, assinatura HMAC, rate limiting). |

## 🏗️ Arquitetura

Projeto único ASP.NET Core / Razor Pages, organizado em camadas por pasta:

```
Core/            entidades, enums, interfaces, exceções de negócio
Application/     serviços de domínio (regras de negócio) e DTOs
Infrastructure/  Npgsql/Dapper, contexto de tenant, fila de webhook, background jobs
Pages/           Razor Pages e PageModels
Models/          modelos do cliente Supabase (Postgrest) — usados só no login
```

Todas as lojas (tenants) compartilham o mesmo schema Postgres; o isolamento é feito por `tenant_id` em cada tabela e aplicado explicitamente em toda query via `ITenantProvider`. O acesso transacional roda via Dapper + Npgsql; o cliente REST do Supabase (Postgrest) é usado apenas na autenticação.

## 💻 Stack Tecnológico

* **Linguagem & Framework:** C# 10 sobre ASP.NET Core 10 (Razor Pages)
* **Banco de Dados & Auth:** Supabase (PostgreSQL + Supabase Auth)
* **Front-end:** HTML5, CSS3, Bootstrap 5

## 🚧 Status do Projeto

O sistema cobre o fluxo operacional completo de uma loja (PDV, turno, estoque, fiado, comandas, dashboard, relatórios) e está **hospedado e rodando em produção** em [pendurai.vinnisantos.com.br](https://pendurai.vinnisantos.com.br) (AWS EC2, HTTPS válido), com cobrança recorrente automatizada via Asaas testada de ponta a ponta no ambiente real.

**Já resolvido** (itens que antes apareciam como pendência):
- ✅ SMTP próprio para e-mails transacionais (Resend)
- ✅ Proteção contra força bruta no login e na senha de supervisor
- ✅ Cobrança automatizada por assinatura via Asaas, em produção
- ✅ Hospedagem e deploy público (AWS EC2 + domínio + HTTPS)

**Ainda em aberto** antes de uma venda comercial mais ampla:
- Testes automatizados (prioridade atual)
- Revisão de segurança formal (pentest/auditoria externa)
- Onboarding self-service de loja (autocadastro público, sem depender de superadmin)
- Emissão fiscal (NFC-e) real e integração de pagamento (maquininha/Pix) no PDV — **conscientemente adiadas por custo de API de terceiros**, não são bugs

A lista completa e priorizada de pendências está em [`pendencias.txt`](./pendencias.txt).

## 🔒 Status do Repositório

Este é um repositório **privado** de caráter proprietário.

O código-fonte, a modelagem de banco de dados e as regras de negócio estruturadas aqui são de uso exclusivo. Não há licença pública de código aberto associada a este projeto, sendo proibida a cópia, distribuição, modificação ou uso comercial por terceiros não autorizados.

Desenvolvido por **Vinnicius Gabriel**.
