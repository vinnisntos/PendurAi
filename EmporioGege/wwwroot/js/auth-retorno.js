// Retorno dos links de e-mail do Supabase Auth (confirmação de cadastro, recuperação de senha).
// O Supabase devolve o resultado no fragmento (#access_token=... ou #error=...) - às vezes na
// query string - e o fragmento nunca chega ao servidor, então quem decide o que mostrar é aqui.
(function () {
    "use strict";

    var params = new URLSearchParams(window.location.hash.replace(/^#/, ""));
    new URLSearchParams(window.location.search).forEach(function (valor, chave) {
        if (!params.has(chave)) params.set(chave, valor);
    });

    var estado;
    if (params.get("error") || params.get("error_code")) {
        var codigo = params.get("error_code") || params.get("error");
        estado = codigo === "otp_expired" || codigo === "access_denied" ? "expirado" : "erro";
    } else if (params.get("access_token") || params.get("code")) {
        estado = "ok";
    } else {
        estado = "erro";
    }

    // Repassa o token pro formulário de nova senha (POST, nunca em URL).
    var campoToken = document.querySelector("[data-pa-access-token]");
    if (campoToken && estado === "ok") campoToken.value = params.get("access_token") || "";

    // Tira os tokens da barra de endereço/histórico.
    if (window.location.hash || window.location.search) {
        history.replaceState(null, "", window.location.pathname);
    }

    document.querySelectorAll("[data-pa-estado]").forEach(function (el) {
        el.hidden = el.getAttribute("data-pa-estado") !== estado;
    });
})();
