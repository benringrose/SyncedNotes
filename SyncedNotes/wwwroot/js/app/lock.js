/* SyncedNotes – app lock (privacy screen). The lock is also enforced by the server: while locked, the
 * session cookie carries a "locked" claim and every notes API returns 423 until a passkey or PIN is verified. */
(function ($) {
    'use strict';
    const { store, ui, db } = SN;
    const pk = SN.passkeys;

    const PIN_ITERATIONS = 150000;
    const MAX_LOCAL_PIN_FAILURES = 5;
    let locked = false;
    let session = null;
    let settings = { enabled: false, timeoutMinutes: 5, lockOnLaunch: true, hasPin: false, pinAvailable: false };
    let lastActivity = Date.now();
    let hiddenAt = null;
    let lastBroadcast = 0;

    const isNetworkError = (e) => !e.status && (e instanceof TypeError || !navigator.onLine || e.offline);

    // ------------------------------------------------------------------ Screen
    function showScreen() {
        document.body.classList.add('app-locked');
        $('#lockScreen').prop('hidden', false);
        $('#lockUser').text(session ? `Signed in as ${session.displayName || session.userName}` : '');
        $('#lockError').prop('hidden', true);
        $('#lockInfo').prop('hidden', navigator.onLine).text('You\'re offline. You can still unlock with your passkey or PIN on this device.');
        refreshPinSection();
        document.activeElement && document.activeElement.blur && document.activeElement.blur();
        setTimeout(() => $('#btnUnlockPasskey').trigger('focus'), 50);
        $('.modal.show').each(function () { const m = bootstrap.Modal.getInstance(this); if (m) m.hide(); });
        ui.closeContextMenu();
    }

    async function refreshPinSection() {
        const local = await db.getMeta('localPin').catch(() => null);
        const show = navigator.onLine ? settings.pinAvailable : !!local;
        $('#pinSection').prop('hidden', !show);
        $('#pinInput').val('');
    }

    function hideScreen() {
        document.body.classList.remove('app-locked');
        $('#lockScreen').prop('hidden', true);
    }

    function showError(message) {
        $('#lockError').text(message).prop('hidden', false);
    }

    function busy($btn, on) {
        if (on) $btn.data('html', $btn.html()).prop('disabled', true).html('<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Checking…');
        else $btn.prop('disabled', false).html($btn.data('html') || $btn.html());
    }

    // ------------------------------------------------------------------ Lock / unlock
    async function lock(opts) {
        opts = opts || {};
        if (locked) return;
        locked = true;
        try { if (SN.editor) await SN.editor.flush(); } catch { /* ignore */ }
        try { sessionStorage.removeItem('sn.unlocked'); } catch { /* ignore */ }
        showScreen();
        if (opts.broadcast !== false) store.broadcast({ type: 'lock' });
        if (opts.reason !== 'server' && opts.reason !== 'other-tab') {
            try { await ui.api('POST', '/api/lock'); } catch { /* offline: the lock is client-side until we reconnect */ }
        }
    }

    function unlocked(opts) {
        opts = opts || {};
        locked = false;
        lastActivity = Date.now();
        hideScreen();
        try { sessionStorage.setItem('sn.unlocked', '1'); } catch { /* ignore */ }
        if (opts.broadcast !== false) store.broadcast({ type: 'unlock' });
        if (SN.syncer) SN.syncer.request(200);
    }

    async function unlockWithPasskey() {
        const $btn = $('#btnUnlockPasskey');
        $('#lockError').prop('hidden', true);
        busy($btn, true);
        try {
            const options = await pk.postJson('/api/unlock/options', {});
            const credential = await pk.get(options);
            const result = await pk.postJson('/api/unlock/passkey', { credentialJson: credential.json });
            if (result && result.appLock) updateSettings(result.appLock);
            unlocked();
        } catch (e) {
            if (e.status) {
                showError(e.message);
            } else if (isNetworkError(e)) {
                // Offline: ask the authenticator for one of this account's passkeys (biometrics / device PIN).
                try {
                    if (await pk.verifyLocally(session && session.passkeyIds, session && session.rpId)) unlocked();
                    else showError('That passkey doesn\'t belong to this account.');
                } catch (e2) {
                    showError(pk.friendlyError(e2));
                }
            } else {
                showError(pk.friendlyError(e));
            }
        } finally {
            busy($btn, false);
        }
    }

    async function unlockWithPin(pin) {
        $('#lockError').prop('hidden', true);
        if (!/^\d{4,12}$/.test(pin)) { showError('Enter your 4–12 digit PIN.'); return; }
        const $btn = $('#btnUnlockPin');
        busy($btn, true);
        try {
            if (navigator.onLine) {
                try {
                    const result = await pk.postJson('/api/unlock/pin', { pin });
                    if (result && result.appLock) updateSettings(result.appLock);
                    await rememberPinLocally(pin);
                    unlocked();
                    return;
                } catch (e) {
                    if (e.status) {
                        showError(e.message);
                        if (e.status === 403) { settings.pinAvailable = false; refreshPinSection(); }
                        $('#pinInput').val('').trigger('focus');
                        return;
                    }
                    // Network error: fall back to the copy of the PIN check stored on this device.
                }
            }
            const local = await db.getMeta('localPin');
            if (!local) { showError('Connect to the internet to unlock with your PIN, or use your passkey.'); return; }
            if (await verifyLocalPin(pin, local)) {
                local.failures = 0;
                await db.setMeta('localPin', local);
                unlocked();
            } else {
                local.failures = (local.failures || 0) + 1;
                if (local.failures >= MAX_LOCAL_PIN_FAILURES) {
                    await db.deleteMeta('localPin');
                    showError('Too many incorrect PINs. Unlock with your passkey.');
                    refreshPinSection();
                } else {
                    await db.setMeta('localPin', local);
                    showError(`Incorrect PIN. ${MAX_LOCAL_PIN_FAILURES - local.failures} attempts left.`);
                }
                $('#pinInput').val('').trigger('focus');
            }
        } finally {
            busy($btn, false);
        }
    }

    // ------------------------------------------------------------------ Local PIN verifier (for offline unlock)
    const toB64 = (bytes) => btoa(String.fromCharCode(...bytes));
    const fromB64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));

    async function derive(pin, salt, iterations) {
        const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(pin), 'PBKDF2', false, ['deriveBits']);
        return new Uint8Array(await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt, iterations }, key, 256));
    }

    async function rememberPinLocally(pin) {
        if (!window.crypto || !crypto.subtle) return;
        const salt = crypto.getRandomValues(new Uint8Array(16));
        const hash = await derive(pin, salt, PIN_ITERATIONS);
        await db.setMeta('localPin', { salt: toB64(salt), hash: toB64(hash), iterations: PIN_ITERATIONS, failures: 0 });
    }

    async function verifyLocalPin(pin, local) {
        const hash = await derive(pin, fromB64(local.salt), local.iterations || PIN_ITERATIONS);
        const expected = fromB64(local.hash);
        if (hash.length !== expected.length) return false;
        let diff = 0;
        for (let i = 0; i < hash.length; i++) diff |= hash[i] ^ expected[i];
        return diff === 0;
    }

    // ------------------------------------------------------------------ Activity tracking
    function touch(broadcast) {
        lastActivity = Date.now();
        if (broadcast !== false && lastActivity - lastBroadcast > 30000) {
            lastBroadcast = lastActivity;
            store.broadcast({ type: 'activity' });
        }
    }

    function check() {
        if (locked || !settings.enabled) return;
        const t = Number(settings.timeoutMinutes);
        if (t > 0 && Date.now() - lastActivity > t * 60000) lock({ reason: 'idle' });
    }

    function bind() {
        ['pointerdown', 'keydown', 'wheel', 'touchstart'].forEach((ev) =>
            document.addEventListener(ev, () => { if (!locked) touch(); }, { passive: true, capture: true }));
        setInterval(check, 10000);
        document.addEventListener('visibilitychange', () => {
            if (!settings.enabled || locked) return;
            const t = Number(settings.timeoutMinutes);
            if (document.visibilityState === 'hidden') {
                hiddenAt = Date.now();
                if (t === 0) lock({ reason: 'hidden' });
            } else if (hiddenAt) {
                if (t > 0 && Date.now() - hiddenAt > t * 60000) lock({ reason: 'idle' });
                hiddenAt = null;
            }
        });
        window.addEventListener('online', () => { if (locked) { $('#lockInfo').prop('hidden', true); refreshPinSection(); } });
        window.addEventListener('offline', () => { if (locked) { $('#lockInfo').prop('hidden', false); refreshPinSection(); } });

        $('#btnUnlockPasskey').on('click', unlockWithPasskey);
        $('#pinForm').on('submit', (e) => { e.preventDefault(); unlockWithPin(String($('#pinInput').val() || '').trim()); });
        $('#btnLockSignOut').on('click', () => SN.commands.run('account.signout'));
    }

    function updateSettings(next) {
        settings = { ...settings, ...next };
        if (session) {
            session.appLock = settings;
            db.setMeta('session', session).catch(() => { });
        }
    }

    function init(sess) {
        session = sess;
        settings = { ...settings, ...(sess && sess.appLock) };
        bind();
    }

    SN.lock = {
        init, lock, unlocked, touch, updateSettings, rememberPinLocally,
        isLocked: () => locked,
        settings: () => ({ ...settings }),
        forgetLocalPin: () => db.deleteMeta('localPin'),
        setSession(sess) { session = sess; updateSettings(sess.appLock || {}); },
    };
})(jQuery);
