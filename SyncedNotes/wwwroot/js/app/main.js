/* SyncedNotes – application bootstrap, routing and deep links. */
(function ($) {
    'use strict';
    const { util, store, ui, db } = SN;

    let session = null;
    let pendingOpen = {};
    const createdHere = new Set();

    // ------------------------------------------------------------------ Routing (#/view[/id][/note/<id>])
    function parseHash() {
        const parts = location.hash.replace(/^#\/?/, '').split('/').filter((p, i) => p || i === 0).map((p) => { try { return decodeURIComponent(p); } catch { return p; } });
        let view = { type: 'all', id: null };
        let i = 0;
        if (['pinned', 'unfiled', 'archive', 'trash', 'all'].includes(parts[0])) { view = { type: parts[0], id: null }; i = 1; }
        else if ((parts[0] === 'binder' || parts[0] === 'tag') && parts[1]) { view = { type: parts[0], id: parts[1] }; i = 2; }
        const noteId = parts[i] === 'note' ? parts[i + 1] || null : null;
        return { view, noteId };
    }

    function hashFor(view, noteId) {
        let h = '#/';
        if (view.type === 'binder' || view.type === 'tag') h += `${view.type}/${encodeURIComponent(view.id)}`;
        else if (view.type && view.type !== 'all') h += view.type;
        if (noteId) h += `${h.endsWith('/') ? '' : '/'}note/${noteId}`;
        return h;
    }

    /** Updates the URL and applies the route synchronously (so focus lands before the user's next keystroke). */
    function setHash(hash, replace) {
        if (location.hash !== hash && !(hash === '#/' && !location.hash)) {
            if (replace) history.replaceState(null, '', hash); else history.pushState(null, '', hash);
        }
        applyRoute();
    }

    function applyRoute() {
        const { view, noteId } = parseHash();
        const current = store.state.view;
        if (view.type === 'binder' && !store.getBinder(view.id)) { history.replaceState(null, '', '#/'); return applyRoute(); }
        if (current.type !== view.type || current.id !== view.id) store.setView(view);
        const opts = pendingOpen;
        pendingOpen = {};
        const previous = SN.editor.currentId();
        if (noteId) {
            if (store.getNote(noteId)) {
                if (previous !== noteId || opts.focus) SN.editor.open(noteId, opts);
            } else {
                ui.toast('That note isn\'t on this device yet. It will appear after the next sync.', { type: 'warning' });
                history.replaceState(null, '', hashFor(view));
                if (previous) SN.editor.close();
            }
        } else if (previous) {
            SN.editor.close();
        }
        if (previous && previous !== noteId) discardIfEmpty(previous);
    }

    /** Removes a note created in this session if it was left completely empty. */
    function discardIfEmpty(id) {
        if (!createdHere.has(id)) return;
        const n = store.getNote(id);
        if (n && !(n.title || '').trim() && !(n.content || '').trim() && !(n.tags || []).length) {
            createdHere.delete(id);
            store.deleteNotesForever([id]);
        }
    }

    // ------------------------------------------------------------------ App API used by other modules
    const app = {
        session: () => session,

        openNote(id, opts) {
            pendingOpen = opts || {};
            const view = store.state.view;
            const note = store.getNote(id);
            // Opening a note that isn't part of the current view (e.g. from the palette) switches to a view that shows it.
            let target = view;
            if (note && !store.inView(note, view)) target = note.trashedAt ? { type: 'trash' } : note.archived ? { type: 'archive' } : { type: 'all' };
            setHash(hashFor(target, id), !!(opts && opts.replace));
        },

        closeNote() { setHash(hashFor(store.state.view), false); },

        go(view) {
            store.setSearch('');
            $('#searchInput').val('');
            setHash(hashFor(view), false);
        },

        async newNote(init) {
            await SN.editor.flush();
            const view = store.state.view;
            if (view.type === 'trash' || view.type === 'archive') history.replaceState(null, '', '#/');
            if (store.state.search) { store.setSearch(''); $('#searchInput').val(''); }
            if (view.type === 'trash' || view.type === 'archive') applyRoute();
            const note = await store.createNote(init || {});
            createdHere.add(note.id);
            app.openNote(note.id, { focus: init && init.title ? 'content' : 'title' });
            return note;
        },

        stepNote(delta) {
            const { pinned, others } = store.visibleNotes();
            const all = [...pinned, ...others];
            if (!all.length) return;
            const idx = all.findIndex((n) => n.id === SN.editor.currentId());
            const next = all[util.clamp(idx < 0 ? 0 : idx + delta, 0, all.length - 1)];
            app.openNote(next.id, { replace: true });
        },

        focusSearch() {
            if (window.matchMedia('(max-width: 767.98px)').matches && SN.editor.currentId()) app.closeNote();
            document.body.classList.remove('focus-mode');
            // Focus synchronously so keys typed straight after "/" land in the box.
            const input = document.getElementById('searchInput');
            input.focus();
            input.select();
        },

        toggleSidebar() {
            if (window.matchMedia('(min-width: 992px)').matches) {
                document.body.classList.toggle('sidebar-hidden');
            } else {
                bootstrap.Offcanvas.getOrCreateInstance(document.getElementById('sidebar')).toggle();
            }
        },

        setTheme(pref) {
            window.SNTheme.set(pref);
            store.setPrefs({ theme: pref });
            ui.toast(`Theme: ${window.SNTheme.label(pref)}`, { timeout: 1500, icon: 'circle-half' });
        },

        sessionExpired() {
            if ($('#appBanner .session-expired').length) return;
            $('#appBanner').prop('hidden', false).html(`<div class="app-banner session-expired"><i class="bi bi-person-exclamation"></i>
                <span>Your session has expired. Your notes are safe on this device – sign in again to sync them.</span>
                <a class="btn btn-sm btn-warning" href="/Account/Login?ReturnUrl=%2F">Sign in</a></div>`);
        },

        async clearLocalData() {
            try { await db.wipe(); } catch { /* ignore */ }
            await SN.pwa.clearCaches();
            try { sessionStorage.clear(); } catch { /* ignore */ }
        },

        async resync() {
            await SN.editor.flush();
            await SN.syncer.now();
            if (await SN.sync.pendingCount()) {
                ui.toast('Some changes couldn\'t be synced yet. Try again when you\'re online.', { type: 'warning' });
                return;
            }
            await db.tx(['notes', 'binders', 'meta'], 'readwrite', (s) => { s.notes.clear(); s.binders.clear(); s.meta.delete('cursor'); });
            await store.reloadFromDb('remote');
            await SN.syncer.now();
            ui.toast('Everything was downloaded again from the server.', { type: 'success' });
        },

        async refreshSession(displayName) {
            try {
                const fresh = await ui.api('GET', '/api/session');
                session = fresh;
                await db.setMeta('session', fresh);
                SN.lock.setSession(fresh);
            } catch {
                if (displayName && session) session.displayName = displayName;
            }
            renderUser();
        },
    };
    SN.app = app;

    function renderUser() {
        if (!session) return;
        const name = session.displayName || session.userName || '';
        const initials = name.split(/[\s._@-]+/).filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join('') || '?';
        $('#userAvatar').text(initials).attr('title', name);
        $('#userMenuName').text(name === session.userName ? name : `${name} (${session.userName})`);
    }

    // ------------------------------------------------------------------ Deep links
    async function createFromParams(params) {
        let title = (params.get('title') || '').trim();
        let text = (params.get('text') || '').replace(/\r\n?/g, '\n').trim();
        const link = (params.get('url') || '').trim();
        if (link && !text.includes(link)) text = text ? `${text}\n\n${link}` : link;
        const tags = (params.get('tags') || '').split(',').map(util.normalizeTag).filter(Boolean);
        const binderName = (params.get('binder') || '').trim().toLowerCase();
        const binder = binderName ? store.listBinders().find((b) => b.name.toLowerCase() === binderName) : null;
        if (!title && !text) return app.newNote({ tags, binderId: binder ? binder.id : null });
        if (!title && !text.includes('\n') && text.length <= 120) { title = text; text = ''; }
        else if (!title) title = util.deriveTitle(text);
        if (params.get('confirm') === '1') {
            // The link came from another website (see QuickAddLinkGuardMiddleware): ask before saving anything.
            const excerpt = text.length > 400 ? `${text.slice(0, 400)}…` : text;
            const ok = await ui.confirm({
                title: 'Add this note?',
                bodyHtml: `<p class="text-body-secondary small">A link asked SyncedNotes to create this note.</p>
                    <div class="border rounded p-2"><strong>${util.escapeHtml(title || 'Untitled')}</strong>
                    ${excerpt ? `<div class="small mt-1" style="white-space:pre-wrap">${util.escapeHtml(excerpt)}</div>` : ''}</div>`,
                okText: 'Add note',
            });
            if (!ok) return null;
        }
        const note = await store.createNote({ title, content: text, tags, binderId: binder ? binder.id : null });
        app.openNote(note.id, { focus: 'content', caret: 'end' });
        ui.toast('Note added', {
            type: 'success', action: 'Undo',
            onAction: async () => { app.closeNote(); await store.deleteNotesForever([note.id]); },
        });
        return note;
    }

    async function handleDeepLink() {
        const url = new URL(location.href);
        const path = url.pathname.toLowerCase();
        const params = url.searchParams;
        const clean = (hash) => history.replaceState(null, '', `/${hash || location.hash || ''}`);

        if (path === '/new' || path === '/share') {
            clean('#/');
            applyRoute();
            await createFromParams(params);
            return;
        }
        if (path === '/open-file') { clean(); return; }
        if (params.get('search') === '1') { clean(); app.focusSearch(); }
        if (params.has('q')) {
            const q = params.get('q') || '';
            clean();
            $('#searchInput').val(q);
            store.setSearch(q);
        }
        if (params.get('recovered') === '1') {
            clean();
            setTimeout(() => {
                SN.settings.open('security');
                ui.toast('You signed in with a recovery code. Add a new passkey so you can sign in next time.', { type: 'warning', timeout: 0 });
            }, 400);
        }
        if (params.has('source')) clean();
    }

    /**
     * Files opened with the installed app. Launch URLs need no handling here: the manifest's launch_handler uses
     * "navigate-existing", so the window navigates to the URL and handleDeepLink() processes it.
     */
    async function handleLaunch(launchParams) {
        if (!launchParams.files || !launchParams.files.length) return;
        const files = await Promise.all(launchParams.files.map((h) => h.getFile()));
        const notes = await store.importFiles(files);
        if (notes.length) {
            app.openNote(notes[0].id);
            ui.toast(notes.length === 1 ? `Opened “${notes[0].title}” as a new note` : `Imported ${notes.length} notes`, { type: 'success' });
        }
    }

    // ------------------------------------------------------------------ Sync status
    function renderSyncStatus(s) {
        let state, icon, text;
        if (s.status === 'syncing') { state = 'syncing'; icon = 'bi-arrow-repeat'; text = 'Syncing…'; }
        else if (s.status === 'offline') { state = 'offline'; icon = 'bi-cloud-slash'; text = s.pending ? `Offline · ${s.pending}` : 'Offline'; }
        else if (s.status === 'unauthorized') { state = 'error'; icon = 'bi-person-exclamation'; text = 'Sign in'; }
        else if (s.status === 'error') { state = 'error'; icon = 'bi-exclamation-triangle'; text = 'Retry sync'; }
        else if (s.pending) { state = 'pending'; icon = 'bi-cloud-arrow-up'; text = `${s.pending} to sync`; }
        else { state = 'synced'; icon = 'bi-cloud-check'; text = 'Synced'; }
        const detail = s.error ? s.error.message
            : s.pending ? `${s.pending} change${s.pending === 1 ? '' : 's'} waiting to sync`
                : s.lastSyncAt ? `Last synced ${util.formatRelative(s.lastSyncAt)}` : 'Sync now';
        $('#syncStatus').attr('data-state', state).attr('title', `${detail} (Ctrl+S to sync now)`)
            .html(`<i class="bi ${icon}"></i><span>${util.escapeHtml(text)}</span>`);
    }

    // ------------------------------------------------------------------ Global behaviours
    function bindGlobal() {
        // Search box.
        const search = util.debounce((q) => store.setSearch(q), 120);
        $('#searchInput').on('input', function () { search(this.value); })
            .on('keydown', function (e) {
                if (e.key === 'Escape') { this.value = ''; store.setSearch(''); this.blur(); }
                if (e.key === 'ArrowDown' || e.key === 'Enter') { e.preventDefault(); SN.list.focusSelected(); }
            });

        // Import files chosen with the picker.
        $('#importFileInput').on('change', async function () {
            const notes = await store.importFiles(Array.from(this.files || []));
            this.value = '';
            if (notes.length) {
                ui.toast(`Imported ${notes.length} note${notes.length === 1 ? '' : 's'}`, { type: 'success' });
                app.openNote(notes[0].id);
            }
        });

        // Drop Markdown/text files anywhere to import them (images are handled by the editor).
        let depth = 0;
        const isImportDrag = (dt) => dt && Array.from(dt.items || []).some((i) => i.kind === 'file' && !/^image\//.test(i.type));
        $(window).on('dragenter', (e) => {
            if (!isImportDrag(e.originalEvent.dataTransfer)) return;
            depth++;
            $('#appDropOverlay').prop('hidden', false);
        }).on('dragleave', (e) => {
            if (!isImportDrag(e.originalEvent.dataTransfer)) return;
            depth = Math.max(0, depth - 1);
            if (!depth) $('#appDropOverlay').prop('hidden', true);
        }).on('dragover', (e) => {
            const dt = e.originalEvent.dataTransfer;
            if (dt && Array.from(dt.types || []).includes('Files')) e.preventDefault();
        }).on('drop', async (e) => {
            const dt = e.originalEvent.dataTransfer;
            if (!dt || !dt.files || !dt.files.length) return;
            e.preventDefault();
            depth = 0;
            $('#appDropOverlay').prop('hidden', true);
            const files = Array.from(dt.files).filter((f) => !/^image\//.test(f.type));
            if (!files.length) return;
            const notes = await store.importFiles(files);
            if (notes.length) {
                ui.toast(`Imported ${notes.length} note${notes.length === 1 ? '' : 's'}`, { type: 'success' });
                app.openNote(notes[0].id);
            } else {
                ui.toast('Drop .md or .txt files to import them as notes.', { type: 'warning' });
            }
        });

        // Sync triggers.
        store.on('sync', renderSyncStatus);
        store.on('conflicts', (list) => list.forEach((c) => {
            if (!c.copyId) return;
            ui.toast(`“${c.title || 'Untitled'}” was also edited on another device. Your version was saved as a separate copy.`, {
                type: 'warning', timeout: 12000, action: 'Open copy', onAction: () => app.openNote(c.copyId),
            });
        }));
        store.on('prefs', ({ source }) => {
            if (source === 'remote' && store.state.prefs.theme && store.state.prefs.theme !== window.SNTheme.get()) {
                window.SNTheme.set(store.state.prefs.theme);
            }
        });
        window.addEventListener('online', () => { SN.syncer.request(300); });
        window.addEventListener('offline', () => { SN.syncer.status = 'offline'; SN.syncer.refreshPending(); });
        document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') SN.syncer.request(500); });
        setInterval(() => { if (document.visibilityState === 'visible' && navigator.onLine) SN.syncer.now(); }, 60 * 1000);
        setInterval(() => renderSyncStatus(SN.syncer), 30 * 1000);

        // Resizable note list.
        const $resizer = $('#paneResizer');
        try { const w = Number(localStorage.getItem('sn.listWidth')); if (w) document.documentElement.style.setProperty('--list-width', `${w}px`); } catch { /* ignore */ }
        $resizer.on('pointerdown', (e) => {
            e.preventDefault();
            $resizer.addClass('dragging');
            const left = document.getElementById('listPane').getBoundingClientRect().left;
            const move = (ev) => {
                const w = util.clamp(ev.clientX - left, 260, Math.min(640, window.innerWidth * 0.5));
                document.documentElement.style.setProperty('--list-width', `${w}px`);
            };
            const up = () => {
                $resizer.removeClass('dragging');
                window.removeEventListener('pointermove', move);
                try { localStorage.setItem('sn.listWidth', String(parseInt(getComputedStyle(document.documentElement).getPropertyValue('--list-width'), 10))); } catch { /* ignore */ }
            };
            window.addEventListener('pointermove', move);
            window.addEventListener('pointerup', up, { once: true });
        }).on('keydown', (e) => {
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
            const current = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--list-width'), 10) || 340;
            const w = util.clamp(current + (e.key === 'ArrowRight' ? 20 : -20), 260, 640);
            document.documentElement.style.setProperty('--list-width', `${w}px`);
        });

        $(document).on('sn:theme', () => $('#btnTheme').attr('title', `Theme: ${window.SNTheme.label()} (${ui.formatShortcut(ui.isMac ? 'Ctrl+Alt+T' : 'Alt+T')})`));
    }

    // ------------------------------------------------------------------ Boot
    async function fetchSession() {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 6000);
        try {
            const res = await fetch('/api/session', { headers: { 'X-SyncedNotes': '1', Accept: 'application/json' }, credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            if (res.status === 401) return { unauthorized: true };
            if (!res.ok) return null;
            return await res.json();
        } catch {
            return null;
        } finally {
            clearTimeout(timer);
        }
    }

    async function boot() {
        let cached = null;
        try { cached = await db.getMeta('session'); } catch (e) {
            $('#appBootText').text('This browser is blocking offline storage, which SyncedNotes needs. Check your privacy settings.');
            return;
        }
        const fresh = await fetchSession();
        if (fresh && fresh.unauthorized) {
            location.replace(`/Account/Login?ReturnUrl=${encodeURIComponent(location.pathname + location.search + location.hash)}`);
            return;
        }
        if (fresh) {
            const localUser = await db.getMeta('userId');
            if (localUser && localUser !== fresh.userId) await db.wipe(); // a different account used this device before
            await db.setMeta('userId', fresh.userId);
            await db.setMeta('session', fresh);
        }
        session = fresh || cached;
        if (!session) {
            $('#appBootText').html('You\'re offline and haven\'t used SyncedNotes on this device yet.<br><button type="button" class="btn btn-primary btn-sm mt-3">Try again</button>');
            $('#appBootText button').on('click', () => location.reload());
            return;
        }

        // Lock first, before any note is rendered.
        SN.lock.init(session);
        const lockCfg = session.appLock || {};
        let unlockedThisTab = false;
        try { unlockedThisTab = sessionStorage.getItem('sn.unlocked') === '1'; } catch { /* ignore */ }
        if (session.locked || (lockCfg.enabled && lockCfg.lockOnLaunch && !unlockedThisTab)) {
            await SN.lock.lock({ reason: session.locked ? 'server' : 'launch' });
        } else {
            try { sessionStorage.setItem('sn.unlocked', '1'); } catch { /* ignore */ }
        }

        await store.load();
        await SN.attachments.loadPending();
        if (store.state.prefs.theme && store.state.prefs.theme !== window.SNTheme.get() && !localStorage.getItem('sn.theme')) {
            window.SNTheme.set(store.state.prefs.theme);
        }

        SN.editor.init();
        SN.list.init();
        SN.sidebar.init();
        SN.commands.init();
        SN.settings.init();
        bindGlobal();
        renderUser();

        // Back/forward and links like href="#/binder/…" (applyRoute is idempotent, so double events are harmless).
        window.addEventListener('hashchange', applyRoute);
        window.addEventListener('popstate', applyRoute);
        applyRoute();
        SN.list.render();

        // Reveal the app before handling deep links: they may need to show a dialog.
        $('#appBoot').remove();
        $('#app').prop('hidden', false);
        $(document).trigger('sn:theme');
        await handleDeepLink();

        SN.syncer.enable();
        SN.syncer.now();
        SN.pwa.init();
        SN.pwa.initLaunchQueue(handleLaunch);
        if (!fresh) SN.syncer.status = navigator.onLine ? 'error' : 'offline';
        renderSyncStatus(SN.syncer);
    }

    $(function () {
        boot().catch((e) => {
            console.error(e);
            $('#appBootText').text(`Something went wrong while starting SyncedNotes: ${e.message}`);
        });
    });
})(jQuery);
