/* SyncedNotes – the settings dialog. */
(function ($) {
    'use strict';
    const { util, store, ui } = SN;
    const esc = util.escapeHtml;
    const pk = SN.passkeys;

    const setActive = (sel, value) => $(sel).find('[data-value]').each(function () {
        const on = String($(this).data('value')) === String(value);
        $(this).toggleClass('active', on).attr('aria-pressed', on);
    });

    function renderGeneral() {
        const p = store.state.prefs;
        setActive('#setTheme', window.SNTheme.get());
        setActive('#setViewMode', p.viewMode);
        setActive('#setEditorMode', p.editorMode);
        $('#setSort').val(p.sort);
        $('#setSortDir').val(p.sortDir).prop('disabled', p.sort === 'manual');
        $('#setShowSnippets').prop('checked', p.showSnippets !== false);
        $('#setFont').val(p.font || 'sans');
        $('#setFontSize').val(String(p.fontSize || 15));
        $('#setSpellcheck').prop('checked', p.spellcheck !== false);
        $('#setToolbar').prop('checked', p.toolbar !== false);
        $('#setSyncScroll').prop('checked', p.syncScroll !== false);
    }

    // ------------------------------------------------------------------ Security
    async function loadPasskeys() {
        const $list = $('#passkeyList');
        try {
            const keys = await ui.api('GET', '/api/account/passkeys');
            $list.html(keys.map((k) => `<div class="list-card-row" data-passkey="${esc(k.id)}">
                <i class="bi bi-${k.backedUp ? 'cloud-check' : 'key'} fs-5 text-body-secondary" title="${k.backedUp ? 'Synced passkey (backed up by your password manager)' : 'Device-bound passkey'}"></i>
                <div class="grow"><div class="text-truncate">${esc(k.name)}</div><div class="sub">Added ${esc(util.formatDateTime(k.createdAt))}${k.backedUp ? ' · synced' : ''}</div></div>
                <button type="button" class="btn btn-icon btn-sm" data-rename-passkey title="Rename"><i class="bi bi-pencil"></i></button>
                <button type="button" class="btn btn-icon btn-sm" data-delete-passkey title="Remove" ${keys.length <= 1 ? 'disabled' : ''}><i class="bi bi-trash3"></i></button>
            </div>`).join('') || '<div class="list-card-row text-body-secondary">No passkeys</div>');
        } catch (e) {
            $list.html(`<div class="list-card-row text-body-secondary">${esc(e.message)}</div>`);
        }
    }

    async function addPasskey() {
        const $btn = $('#btnAddPasskey').prop('disabled', true);
        try {
            const options = await ui.api('POST', '/api/account/passkeys/options');
            const credentialJson = await pk.create(options);
            await ui.api('POST', '/api/account/passkeys', { credentialJson, name: pk.defaultName() });
            ui.toast('Passkey added', { type: 'success' });
            await loadPasskeys();
            SN.app.refreshSession();
        } catch (e) {
            ui.toast(e.status || e.offline ? e.message : pk.friendlyError(e), { type: 'danger' });
        } finally {
            $btn.prop('disabled', false);
        }
    }

    function renderLock() {
        const s = SN.lock.settings();
        $('#lockEnabled').prop('checked', !!s.enabled);
        $('#lockTimeout').val(String(s.timeoutMinutes)).prop('disabled', !s.enabled);
        $('#lockOnLaunch').prop('checked', !!s.lockOnLaunch).prop('disabled', !s.enabled);
        $('#pinStatus').text(s.hasPin ? (s.pinAvailable ? 'Set' : 'Disabled after too many attempts – set a new PIN') : 'Not set');
        $('#btnSetPin').text(s.hasPin ? 'Change PIN' : 'Set PIN');
        $('#btnRemovePin').prop('hidden', !s.hasPin);
    }

    async function saveLock() {
        try {
            const result = await ui.api('PUT', '/api/account/app-lock', {
                enabled: $('#lockEnabled').is(':checked'),
                timeoutMinutes: Number($('#lockTimeout').val()),
                lockOnLaunch: $('#lockOnLaunch').is(':checked'),
            });
            SN.lock.updateSettings(result);
            if (result.enabled) { try { sessionStorage.setItem('sn.unlocked', '1'); } catch { /* ignore */ } }
        } catch (e) {
            ui.toast(e.message, { type: 'danger' });
        }
        renderLock();
    }

    async function setPin() {
        const validate = (v) => (/^\d{4,12}$/.test(v) ? null : 'Use 4 to 12 digits.');
        const pin = await ui.prompt({ title: 'Set an unlock PIN', label: 'New PIN (4–12 digits)', type: 'password', inputmode: 'numeric', maxlength: 12, validate, okText: 'Next' });
        if (!pin) return;
        const again = await ui.prompt({ title: 'Confirm your PIN', label: 'Enter the PIN again', type: 'password', inputmode: 'numeric', maxlength: 12, validate, okText: 'Save PIN' });
        if (again === null) return;
        if (again !== pin) { ui.toast('The PINs didn\'t match.', { type: 'warning' }); return; }
        try {
            const result = await ui.api('PUT', '/api/account/app-lock/pin', { pin });
            SN.lock.updateSettings(result);
            await SN.lock.rememberPinLocally(pin);
            ui.toast('PIN saved', { type: 'success' });
        } catch (e) {
            ui.toast(e.message, { type: 'danger' });
        }
        renderLock();
    }

    async function removePin() {
        if (!await ui.confirm({ title: 'Remove your PIN?', body: 'You\'ll unlock with your passkey only.', okText: 'Remove PIN', danger: true })) return;
        try {
            SN.lock.updateSettings(await ui.api('DELETE', '/api/account/app-lock/pin'));
            await SN.lock.forgetLocalPin();
        } catch (e) {
            ui.toast(e.message, { type: 'danger' });
        }
        renderLock();
    }

    async function loadRecovery() {
        try {
            const r = await ui.api('GET', '/api/account/recovery-codes');
            $('#recoveryRemaining').text(r.remaining ? `You have ${r.remaining} unused code${r.remaining === 1 ? '' : 's'}.` : 'You have no unused codes – generate new ones.');
        } catch { $('#recoveryRemaining').text(''); }
    }

    async function newRecoveryCodes() {
        if (!await ui.confirm({ title: 'Generate new recovery codes?', body: 'Your existing recovery codes will stop working.', okText: 'Generate' })) return;
        try {
            const r = await ui.api('POST', '/api/account/recovery-codes');
            $('#newCodesList').html(r.codes.map((c) => `<li>${esc(c)}</li>`).join(''));
            $('#newCodesBox').prop('hidden', false).data('codes', r.codes);
            loadRecovery();
        } catch (e) {
            ui.toast(e.message, { type: 'danger' });
        }
    }

    async function loadProfile() {
        const s = SN.app.session();
        $('#setDisplayName').val((s && s.displayName) || '');
    }

    async function deleteAccount() {
        const s = SN.app.session();
        const typed = await ui.prompt({
            title: 'Delete your account?',
            label: `This permanently deletes your account, every note and image, on all devices. Type your username (${s.userName}) to confirm.`,
            okText: 'Delete everything',
        });
        if (typed === null) return;
        try {
            const r = await ui.api('POST', '/api/account/delete', { confirmUserName: typed });
            await SN.app.clearLocalData();
            location.assign(r.redirect || '/Account/Login');
        } catch (e) {
            ui.toast(e.message, { type: 'danger' });
        }
    }

    // ------------------------------------------------------------------ Integrations
    function renderIntegrations() {
        const origin = location.origin;
        $('#cmdpalNewUrl').val(`${origin}/new?text={note}`);
        $('#cmdpalTitleUrl').val(`${origin}/new?title={title}&text={note}`);
        $('#cmdpalSearchUrl').val(`${origin}/?q={search}`);
        $('#apiBaseUrl').text(origin);
        const code = `(()=>{const s=String(getSelection());location.href='${origin}/new?title='+encodeURIComponent(document.title)+'&text='+encodeURIComponent((s?'> '+s+'\\n\\n':'')+location.href)})()`;
        $('#bookmarklet').attr('href', `javascript:${code}`);
        loadTokens();
    }

    async function loadTokens() {
        const $list = $('#tokenList');
        try {
            const tokens = await ui.api('GET', '/api/account/tokens');
            $list.html(tokens.map((t) => `<div class="list-card-row" data-token="${t.id}">
                <i class="bi bi-key fs-5 text-body-secondary"></i>
                <div class="grow"><div class="text-truncate">${esc(t.name)}</div>
                <div class="sub"><code>${esc(t.prefix)}…</code> · created ${esc(util.formatRelative(t.createdAt))}${t.lastUsedAt ? ` · last used ${esc(util.formatRelative(t.lastUsedAt))}` : ' · never used'}</div></div>
                <button type="button" class="btn btn-sm btn-outline-danger" data-revoke-token>Revoke</button></div>`).join(''))
                .prop('hidden', !tokens.length);
        } catch (e) {
            $list.prop('hidden', false).html(`<div class="list-card-row text-body-secondary">${esc(e.message)}</div>`);
        }
    }

    async function createToken(e) {
        e.preventDefault();
        try {
            const r = await ui.api('POST', '/api/account/tokens', { name: String($('#tokenName').val() || '').trim() || 'Integration' });
            $('#tokenValue').val(r.token);
            $('#tokenReveal').prop('hidden', false);
            $('#tokenName').val('');
            loadTokens();
        } catch (err) {
            ui.toast(err.message, { type: 'danger' });
        }
    }

    // ------------------------------------------------------------------ Data
    async function renderData() {
        const s = SN.syncer;
        const labels = { synced: 'Up to date', syncing: 'Syncing…', offline: 'Offline', error: 'Last sync failed', unauthorized: 'Signed out – sign in to sync', locked: 'Locked', idle: 'Waiting' };
        $('#dataSyncState').text(labels[s.status] || s.status);
        const last = s.lastSyncAt || await SN.db.getMeta('lastSyncAt');
        $('#dataLastSync').text(last ? util.formatDateTime(last) : 'Never');
        $('#dataPending').text(String(await SN.sync.pendingCount()));
        if (navigator.storage && navigator.storage.estimate) {
            const est = await navigator.storage.estimate();
            const persisted = navigator.storage.persisted ? await navigator.storage.persisted() : false;
            $('#storageInfo').text(`Using ${util.formatBytes(est.usage || 0)} of ${util.formatBytes(est.quota || 0)} available.` +
                (persisted ? ' Your notes are protected from automatic cleanup by the browser.' : ' The browser may clear this data if the device runs low on space – your notes are always safe on the server.'));
            $('#btnPersist').prop('hidden', persisted || !navigator.storage.persist);
        } else {
            $('#storageInfo').text('Storage details aren\'t available in this browser.');
            $('#btnPersist').prop('hidden', true);
        }
    }

    // ------------------------------------------------------------------ Wiring
    const loaders = {
        security: () => { renderLock(); loadPasskeys(); loadRecovery(); loadProfile(); },
        integrations: renderIntegrations,
        data: renderData,
    };

    function open(tab) {
        renderGeneral();
        $('#aboutVersion').text((SN.app.session() && SN.app.session().version) || '');
        const target = tab ? `#set-${tab}` : '#set-general';
        const btn = document.querySelector(`#settingsModal [data-bs-target="${target}"]`);
        if (btn) bootstrap.Tab.getOrCreateInstance(btn).show();
        if (tab && loaders[tab]) loaders[tab]();
        ui.modal('settingsModal').show();
    }

    function bind() {
        $('#settingsModal').on('shown.bs.tab', '[data-settings-tab]', function () {
            const fn = loaders[this.dataset.settingsTab];
            if (fn) fn();
        }).on('hidden.bs.modal', () => {
            $('#tokenReveal, #newCodesBox').prop('hidden', true);
            $('#tokenValue').val('');
        });

        $('#setTheme').on('click', '[data-value]', function () { SN.app.setTheme(String($(this).data('value'))); renderGeneral(); });
        $('#setViewMode').on('click', '[data-value]', function () { store.setPrefs({ viewMode: String($(this).data('value')) }); renderGeneral(); });
        $('#setEditorMode').on('click', '[data-value]', function () { SN.editor.setMode(String($(this).data('value')), true); renderGeneral(); });
        $('#setSort').on('change', function () { store.setPrefs({ sort: this.value }); renderGeneral(); });
        $('#setSortDir').on('change', function () { store.setPrefs({ sortDir: this.value }); });
        $('#setShowSnippets').on('change', function () { store.setPrefs({ showSnippets: this.checked }); });
        $('#setFont').on('change', function () { store.setPrefs({ font: this.value }); });
        $('#setFontSize').on('change', function () { store.setPrefs({ fontSize: Number(this.value) }); });
        $('#setSpellcheck').on('change', function () { store.setPrefs({ spellcheck: this.checked }); });
        $('#setToolbar').on('change', function () { store.setPrefs({ toolbar: this.checked }); });
        $('#setSyncScroll').on('change', function () { store.setPrefs({ syncScroll: this.checked }); });

        $('#btnAddPasskey').on('click', addPasskey);
        $('#passkeyList').on('click', '[data-rename-passkey]', async function () {
            const id = $(this).closest('[data-passkey]').data('passkey');
            const current = $(this).closest('[data-passkey]').find('.grow > div').first().text();
            const name = await ui.prompt({ title: 'Rename passkey', label: 'Name', value: current, maxlength: 60, okText: 'Rename' });
            if (!name) return;
            try { await ui.api('PUT', `/api/account/passkeys/${encodeURIComponent(id)}`, { name }); loadPasskeys(); } catch (e) { ui.toast(e.message, { type: 'danger' }); }
        }).on('click', '[data-delete-passkey]', async function () {
            const id = $(this).closest('[data-passkey]').data('passkey');
            if (!await ui.confirm({ title: 'Remove this passkey?', body: 'You won\'t be able to sign in with it any more.', okText: 'Remove', danger: true })) return;
            try { await ui.api('DELETE', `/api/account/passkeys/${encodeURIComponent(id)}`); loadPasskeys(); SN.app.refreshSession(); } catch (e) { ui.toast(e.message, { type: 'danger' }); }
        });
        $('#lockEnabled, #lockTimeout, #lockOnLaunch').on('change', saveLock);
        $('#btnSetPin').on('click', setPin);
        $('#btnRemovePin').on('click', removePin);
        $('#btnNewCodes').on('click', newRecoveryCodes);
        $('#btnCopyNewCodes').on('click', async () => {
            const codes = $('#newCodesBox').data('codes') || [];
            if (await ui.copyText(codes.join('\n'))) ui.toast('Recovery codes copied', { type: 'success', timeout: 2000 });
        });
        $('#btnSaveDisplayName').on('click', async () => {
            try {
                const r = await ui.api('PUT', '/api/account/profile', { displayName: String($('#setDisplayName').val() || '') });
                ui.toast('Display name saved', { type: 'success', timeout: 2000 });
                SN.app.refreshSession(r.displayName);
            } catch (e) { ui.toast(e.message, { type: 'danger' }); }
        });
        $('#btnDeleteAccount').on('click', deleteAccount);

        $('#tokenForm').on('submit', createToken);
        $('#tokenList').on('click', '[data-revoke-token]', async function () {
            const id = $(this).closest('[data-token]').data('token');
            if (!await ui.confirm({ title: 'Revoke this token?', body: 'Anything using it will stop working immediately.', okText: 'Revoke', danger: true })) return;
            try { await ui.api('DELETE', `/api/account/tokens/${id}`); loadTokens(); } catch (e) { ui.toast(e.message, { type: 'danger' }); }
        });
        $('#bookmarklet').on('click', (e) => { e.preventDefault(); ui.toast('Drag this button to your bookmarks bar.', { timeout: 3000 }); });

        $(document).on('click', '[data-copy]', async function () {
            const value = $($(this).data('copy')).val();
            if (value && await ui.copyText(String(value))) ui.toast('Copied to clipboard', { type: 'success', timeout: 1800 });
        });

        $('#btnResync').on('click', async () => {
            if (!await ui.confirm({ title: 'Re-download everything?', body: 'Notes on this device are replaced with the copy on the server. Unsynced changes are pushed first.', okText: 'Re-download' })) return;
            await SN.app.resync();
            renderData();
        });
        $('#btnPersist').on('click', async () => {
            const ok = navigator.storage && navigator.storage.persist ? await navigator.storage.persist() : false;
            ui.toast(ok ? 'Your notes will be kept on this device.' : 'The browser didn\'t allow persistent storage. Installing the app usually helps.', { type: ok ? 'success' : 'warning' });
            renderData();
        });
        store.on('sync', () => { if ($('#set-data').hasClass('active') && $('#settingsModal').hasClass('show')) renderData(); });
    }

    SN.settings = { init: bind, open };
})(jQuery);
