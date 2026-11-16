/* SyncedNotes – sign-in, registration and recovery pages. */
(function ($) {
    'use strict';
    const pk = SN.passkeys;

    /** A fresh sign-in counts as unlocking the app lock for this tab. */
    function markUnlocked() {
        try { sessionStorage.setItem('sn.unlocked', '1'); } catch { /* private mode */ }
    }

    function showAlert(message, type) {
        const $a = $('#authAlert');
        $a.removeClass('d-none alert-danger alert-success alert-info alert-warning').addClass('alert-' + (type || 'danger'));
        $a.text(message);
    }

    function busy($btn, isBusy, label) {
        if (isBusy) {
            $btn.data('label', $btn.html()).prop('disabled', true)
                .html(`<span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>${label || 'Working…'}`);
        } else {
            $btn.prop('disabled', false).html($btn.data('label'));
        }
    }

    function requireSupport() {
        if (!pk.supported()) {
            $('#unsupported').removeClass('d-none');
            $('form button[type=submit]').prop('disabled', true);
            return false;
        }
        if (!window.isSecureContext) {
            showAlert('Passkeys only work over HTTPS (or on localhost). Open this site with https://.', 'warning');
        }
        return true;
    }

    // ----------------------------------------------------------------------- Sign in
    function initLogin() {
        const $form = $('#loginForm');
        if (!$form.length) return;
        const returnUrl = $form.data('returnUrl') || '/';
        if (!requireSupport()) return;
        let conditional = null;

        async function complete(credentialJson) {
            const result = await pk.postJson('/api/auth/login/complete', {
                credentialJson,
                rememberMe: $('#rememberMe').is(':checked'),
                returnUrl,
            });
            markUnlocked();
            window.location.assign(result.redirect || '/');
        }

        async function signIn(event) {
            event.preventDefault();
            if (conditional) { conditional.abort(); conditional = null; }
            const $btn = $('#btnPasskeyLogin');
            busy($btn, true, 'Waiting for your passkey…');
            try {
                const options = await pk.postJson('/api/auth/login/options', { userName: $('#userName').val().trim() || null });
                const credential = await pk.get(options);
                await complete(credential.json);
            } catch (e) {
                busy($btn, false);
                showAlert(e.status ? e.message : pk.friendlyError(e));
            }
        }

        $form.on('submit', signIn);

        // Passkey autofill: offer saved passkeys in the username field's suggestions.
        pk.conditionalMediationAvailable().then(async (available) => {
            if (!available) return;
            try {
                conditional = new AbortController();
                const options = await pk.postJson('/api/auth/login/options', {});
                const credential = await pk.get(options, { mediation: 'conditional', signal: conditional.signal });
                busy($('#btnPasskeyLogin'), true, 'Signing in…');
                await complete(credential.json);
            } catch (e) {
                if (e && e.name !== 'AbortError' && e.status) showAlert(e.message);
            }
        });
    }

    // ----------------------------------------------------------------------- Register
    function initRegister() {
        const $form = $('#registerForm');
        if (!$form.length) return;
        if (!requireSupport()) return;

        $form.on('submit', async (event) => {
            event.preventDefault();
            const $btn = $('#btnRegister');
            const userName = $('#userName').val().trim();
            const displayName = $('#displayName').val().trim();
            if (!userName) { showAlert('Choose a username.'); return; }
            busy($btn, true, 'Creating your passkey…');
            try {
                const options = await pk.postJson('/api/auth/register/options', { userName, displayName });
                const credentialJson = await pk.create(options);
                const result = await pk.postJson('/api/auth/register/complete', { credentialJson, passkeyName: pk.defaultName() });
                markUnlocked();
                showRecoveryCodes(result.recoveryCodes, result.redirect || '/');
            } catch (e) {
                busy($btn, false);
                showAlert(e.status ? e.message : pk.friendlyError(e));
            }
        });
    }

    function showRecoveryCodes(codes, redirect) {
        $('#registerStep').addClass('d-none');
        $('#authAlert').addClass('d-none');
        const $list = $('#recoveryCodeList').empty();
        codes.forEach((c) => $list.append($('<li>').text(c)));
        $('#recoveryStep').removeClass('d-none');
        const text = `SyncedNotes recovery codes for ${location.host}\nEach code can be used once.\n\n${codes.join('\n')}\n`;
        $('#btnCopyCodes').on('click', async () => {
            try { await navigator.clipboard.writeText(text); $('#btnCopyCodes').html('<i class="bi bi-check2"></i> Copied'); } catch { /* ignore */ }
        });
        $('#btnDownloadCodes').on('click', () => {
            const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
            const a = Object.assign(document.createElement('a'), { href: url, download: 'syncednotes-recovery-codes.txt' });
            document.body.appendChild(a); a.click(); a.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        });
        $('#savedCodes').on('change', function () { $('#btnContinue').prop('disabled', !this.checked); });
        $('#btnContinue').on('click', () => window.location.assign(redirect));
    }

    // ----------------------------------------------------------------------- Recovery
    function initRecover() {
        const $form = $('#recoverForm');
        if (!$form.length) return;
        $form.on('submit', async (event) => {
            event.preventDefault();
            const $btn = $('#btnRecover');
            busy($btn, true, 'Checking…');
            try {
                const result = await pk.postJson('/api/auth/recover', {
                    userName: $('#userName').val().trim(),
                    code: $('#code').val().trim(),
                });
                markUnlocked();
                window.location.assign(result.redirect || '/');
            } catch (e) {
                busy($btn, false);
                showAlert(e.message);
            }
        });
    }

    function initThemeToggle() {
        const $btn = $('#btnThemeToggle');
        const render = () => $btn.find('span').text(`Theme: ${window.SNTheme.label()}`);
        $btn.on('click', () => { window.SNTheme.cycle(); render(); });
        render();
    }

    $(function () {
        initThemeToggle();
        initLogin();
        initRegister();
        initRecover();
        $('[data-action=retry]').on('click', () => window.location.reload());
    });
})(jQuery);
