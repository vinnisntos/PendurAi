// PendurAi - comportamento do shell (tema, navegação ativa, confirmações).
// O tema inicial é aplicado por um script inline no <head> pra não piscar.
(function () {
    "use strict";

    var CHAVE_TEMA = "pendurai-tema";

    function lerTemaSalvo() {
        try { return localStorage.getItem(CHAVE_TEMA); } catch (e) { return null; }
    }

    function salvarTema(tema) {
        try { localStorage.setItem(CHAVE_TEMA, tema); } catch (e) { /* modo privado: só não lembra */ }
    }

    function aplicarTema(tema) {
        document.documentElement.setAttribute("data-bs-theme", tema);
        document.querySelectorAll("[data-pa-theme-icon]").forEach(function (el) {
            el.className = tema === "dark" ? "bi bi-sun" : "bi bi-moon-stars";
        });
        document.querySelectorAll("[data-pa-theme-toggle]").forEach(function (el) {
            el.setAttribute("aria-label", tema === "dark" ? "Usar tema claro" : "Usar tema escuro");
            el.setAttribute("title", tema === "dark" ? "Tema claro" : "Tema escuro");
        });
    }

    function marcarNavegacaoAtiva() {
        var caminho = window.location.pathname.toLowerCase().replace(/\/index$/, "").replace(/\/$/, "");
        var melhor = null;
        var melhorTamanho = -1;
        document.querySelectorAll(".app-nav a[href]").forEach(function (link) {
            var href = link.getAttribute("href").toLowerCase().replace(/\/index$/, "").replace(/\/$/, "");
            var casa = caminho === href || caminho.indexOf(href + "/") === 0;
            if (casa && href.length > melhorTamanho) {
                melhor = link;
                melhorTamanho = href.length;
            }
        });
        if (melhor) {
            melhor.classList.add("active");
            melhor.setAttribute("aria-current", "page");
        }
    }

    document.addEventListener("DOMContentLoaded", function () {
        aplicarTema(document.documentElement.getAttribute("data-bs-theme") || "dark");

        document.querySelectorAll("[data-pa-theme-toggle]").forEach(function (botao) {
            botao.addEventListener("click", function () {
                var atual = document.documentElement.getAttribute("data-bs-theme");
                var novo = atual === "dark" ? "light" : "dark";
                salvarTema(novo);
                aplicarTema(novo);
            });
        });

        marcarNavegacaoAtiva();

        // Confirmação declarativa: <button data-confirm="Tem certeza?">
        document.addEventListener("submit", function (ev) {
            var emissor = ev.submitter;
            var mensagem = (emissor && emissor.getAttribute("data-confirm")) || ev.target.getAttribute("data-confirm");
            if (mensagem && !window.confirm(mensagem)) {
                ev.preventDefault();
            }
        });
    });

    // Imprime o recibo pelo navegador num iframe escondido (não é bloqueado como pop-up).
    function imprimirRecibo(vendaId) {
        var quadro = document.createElement("iframe");
        quadro.setAttribute("aria-hidden", "true");
        quadro.style.cssText = "position:fixed;width:0;height:0;border:0;right:0;bottom:0;";
        quadro.src = "/Caixa/Recibo/" + encodeURIComponent(vendaId);
        document.body.appendChild(quadro);
        setTimeout(function () { quadro.remove(); }, 60000);
    }

    function impressoraNoServidor() {
        var meta = document.querySelector('meta[name="pa-impressora-servidor"]');
        return !!meta && meta.content === "true";
    }

    window.PendurAi = window.PendurAi || {};
    window.PendurAi.lerTemaSalvo = lerTemaSalvo;
    window.PendurAi.imprimirRecibo = imprimirRecibo;
    window.PendurAi.impressoraNoServidor = impressoraNoServidor;
})();
