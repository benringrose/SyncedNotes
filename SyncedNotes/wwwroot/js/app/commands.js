/* SyncedNotes – commands, note actions (with undo), context menus, keyboard shortcuts and the palette. */
(function ($) {
    'use strict';
    const { util, store, ui } = SN;
    const esc = util.escapeHtml;
    const isMac = ui.isMac;
    const APP_MOD = isMac ? 'Ctrl Alt' : 'Alt';

    const registry = new Map();
    const register = (cmd) => registry.set(cmd.id, cmd);
    const run = (id, ctx) => {
        const cmd = registry.get(id);
        if (!cmd) return undefined;
        ctx = ctx || {};
        if (cmd.when && !cmd.when(ctx)) return undefined;
        return cmd.run(ctx);
    };

    const currentNote = () => { const id = SN.editor.currentId(); return id ? store.getNote(id) : null; };
    const targetIds = (ctx) => (ctx.ids ? ctx.ids : ctx.id ? [ctx.id] : currentNote() ? [currentNote().id] : []);
    const hasTarget = (ctx) => targetIds(ctx).length > 0;

    // ------------------------------------------------------------------ Undoable note actions
    const ACTION_FIELDS = ['pinned', 'archived', 'trashedAt', 'binderId', 'color', 'icon', 'tags', 'sortOrder'];
    const history = { done: [], undone: [] };
    const pick = (n) => { const o = {}; ACTION_FIELDS.forEach((f) => { o[f] = Array.isArray(n[f]) ? n[f].slice() : n[f]; }); return o; };

    async function perform(label, ids, patchFn, opts) {
        ids = ids.filter((id) => store.getNote(id));
        if (!ids.length) return;
        const before = new Map(ids.map((id) => [id, pick(store.getNote(id))]));
        await store.updateNotes(ids, patchFn);
        const after = new Map(ids.map((id) => [id, pick(store.getNote(id) || {})]));
        history.done.push({ label, before, after });
        if (history.done.length > 50) history.done.shift();
        history.undone = [];
        if (!opts || opts.toast !== false) ui.toast(label, { action: 'Undo', onAction: undoAction });
    }

    async function undoAction() {
        const entry = history.done.pop();
        if (!entry) { ui.toast('Nothing to undo', { timeout: 1500 }); return; }
        await store.updateNotes([...entry.before.keys()], (n) => entry.before.get(n.id));
        history.undone.push(entry);
        ui.toast(`Undone: ${entry.label}`, { action: 'Redo', onAction: redoAction, timeout: 4000 });
    }

    async function redoAction() {
        const entry = history.undone.pop();
        if (!entry) { ui.toast('Nothing to redo', { timeout: 1500 }); return; }
        await store.updateNotes([...entry.after.keys()], (n) => entry.after.get(n.id));
        history.done.push(entry);
        ui.toast(`Redone: ${entry.label}`, { timeout: 2500 });
    }

    const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
    const noun = (ids) => (ids.length === 1 ? 'Note' : plural(ids.length, 'note'));

    function closeIfOpen(ids) {
        const open = SN.editor.currentId();
        if (open && ids.includes(open) && !store.inView(store.getNote(open) || {}, store.state.view)) SN.app.closeNote();
    }

    const actions = {
        async pin(ids) {
            const pin = !ids.every((id) => store.getNote(id) && store.getNote(id).pinned);
            await perform(`${noun(ids)} ${pin ? 'pinned' : 'unpinned'}`, ids, () => (pin ? { pinned: true, archived: false, trashedAt: null } : { pinned: false }), { toast: ids.length > 1 });
        },
        async archive(ids) {
            const archive = !ids.every((id) => store.getNote(id) && store.getNote(id).archived);
            await perform(`${noun(ids)} ${archive ? 'archived' : 'unarchived'}`, ids, () => (archive ? { archived: true, pinned: false, trashedAt: null } : { archived: false }));
            closeIfOpen(ids);
        },
        async trash(ids) {
            const now = Date.now();
            await perform(`${noun(ids)} moved to trash`, ids, () => ({ trashedAt: now, pinned: false }));
            closeIfOpen(ids);
        },
        async restore(ids) {
            await perform(`${noun(ids)} restored`, ids, () => ({ trashedAt: null }));
            closeIfOpen(ids);
        },
        async deleteForever(ids) {
            const ok = await ui.confirm({
                title: ids.length === 1 ? 'Delete this note forever?' : `Delete ${ids.length} notes forever?`,
                body: 'This can\'t be undone. The note will be removed from all your devices.',
                okText: 'Delete forever', danger: true,
            });
            if (!ok) return;
            if (ids.includes(SN.editor.currentId())) SN.app.closeNote();
            await store.deleteNotesForever(ids);
            ui.toast(`${noun(ids)} deleted forever`, { type: 'success' });
        },
        async move(ids, binderId) {
            const binder = binderId ? store.getBinder(binderId) : null;
            await perform(`${noun(ids)} moved to ${binder ? binder.name : 'Unfiled'}`, ids, () => ({ binderId: binderId || null, trashedAt: null }));
            closeIfOpen(ids);
        },
        async addTag(ids, tag) {
            tag = util.normalizeTag(tag);
            if (!tag) return;
            await perform(`Tagged #${tag}`, ids, (n) => ((n.tags || []).some((t) => t.toLowerCase() === tag.toLowerCase()) ? {} : { tags: [...(n.tags || []), tag] }));
        },
        async style(ids) {
            const first = store.getNote(ids[0]);
            if (!first) return;
            const result = await ui.pickStyle({ title: 'Colour & icon', color: first.color, icon: first.icon });
            if (!result) return;
            await perform('Style updated', ids, () => ({ color: result.color, icon: result.icon }), { toast: false });
        },
        async duplicate(id) {
            const n = store.getNote(id);
            if (!n) return;
            const copy = await store.createNote({
                title: `${n.title || 'Untitled'} (copy)`, content: n.content, binderId: n.binderId, tags: n.tags,
                color: n.color, icon: n.icon, pinned: false, sortOrder: n.sortOrder - 0.5,
            });
            SN.app.openNote(copy.id, { focus: 'title' });
            ui.toast('Note duplicated', { type: 'success', timeout: 2000 });
        },
        async emptyTrash() {
            const ids = Array.from(store.state.notes.values()).filter((n) => !n.deleted && n.trashedAt).map((n) => n.id);
            if (!ids.length) return;
            const ok = await ui.confirm({ title: 'Empty the trash?', body: `${plural(ids.length, 'note')} will be deleted forever on all your devices.`, okText: 'Empty trash', danger: true });
            if (!ok) return;
            if (ids.includes(SN.editor.currentId())) SN.app.closeNote();
            await store.deleteNotesForever(ids);
            ui.toast('Trash emptied', { type: 'success' });
        },
    };

    /** Handles a note dropped on a sidebar target ("binder:<id>", "tag:<name>", "archive", ...). */
    function dropNote(ids, target) {
        if (!target) return undefined;
        if (target.startsWith('binder:')) return actions.move(ids, target.slice(7));
        if (target.startsWith('tag:')) return actions.addTag(ids, target.slice(4));
        switch (target) {
            case 'pinned': return perform(`${noun(ids)} pinned`, ids, () => ({ pinned: true, archived: false, trashedAt: null }));
            case 'unfiled': return actions.move(ids, null);
            case 'archive': return perform(`${noun(ids)} archived`, ids, () => ({ archived: true, pinned: false, trashedAt: null })).then(() => closeIfOpen(ids));
            case 'trash': return actions.trash(ids);
            case 'all': return perform(`${noun(ids)} restored`, ids, () => ({ archived: false, trashedAt: null }));
            default: return undefined;
        }
    }

    function reorder(ids, orders, pinned) {
        const map = new Map(ids.map((id, i) => [id, orders[i]]));
        return perform('Note moved', ids, (n) => ({ sortOrder: map.get(n.id), pinned }), { toast: false });
    }

    // ------------------------------------------------------------------ Binders & tags
    async function newBinder(ctx) {
        const result = await ui.pickStyle({ title: 'New binder', withName: true, okText: 'Create', icon: 'bi-folder2', color: 'blue' });
        if (!result) return null;
        const binder = await store.createBinder({ name: result.name, color: result.color, icon: result.icon });
        if (ctx && ctx.source && ctx.source.dataset && ctx.source.dataset.assignNote && SN.editor.currentId()) {
            await store.updateNote(SN.editor.currentId(), { binderId: binder.id });
        } else {
            SN.app.go({ type: 'binder', id: binder.id });
        }
        ui.toast(`Binder “${binder.name}” created`, { type: 'success', timeout: 2500 });
        return binder;
    }

    async function editBinder(id) {
        const b = store.getBinder(id);
        if (!b) return;
        const result = await ui.pickStyle({ title: 'Edit binder', withName: true, name: b.name, color: b.color, icon: b.icon });
        if (result) await store.updateBinder(id, { name: result.name, color: result.color, icon: result.icon });
    }

    async function deleteBinder(id) {
        const b = store.getBinder(id);
        if (!b) return;
        const count = store.counts().binders[id] || 0;
        const ok = await ui.confirm({
            title: `Delete “${b.name}”?`,
            bodyHtml: count
                ? `<p>${plural(count, 'note')} in this binder will be kept and moved to <strong>Unfiled</strong>.</p>
                   <div class="form-check"><input class="form-check-input" type="checkbox" id="deleteBinderNotes"><label class="form-check-label" for="deleteBinderNotes">Move its notes to the trash instead</label></div>`
                : '<p>The binder is empty.</p>',
            okText: 'Delete binder', danger: true,
        });
        if (!ok) return;
        const trashNotes = $('#deleteBinderNotes').is(':checked');
        if (store.state.view.type === 'binder' && store.state.view.id === id) SN.app.go({ type: 'all' });
        await store.deleteBinder(id, { trashNotes });
        ui.toast(`Binder “${b.name}” deleted`, { type: 'success' });
    }

    async function renameTag(name) {
        const next = await ui.prompt({ title: 'Rename tag', label: 'New name', value: name, okText: 'Rename' });
        if (!next || next === name) return;
        await store.renameTag(name, next);
        if (store.state.view.type === 'tag') SN.app.go({ type: 'tag', id: util.normalizeTag(next) });
        ui.toast(`Tag renamed to #${util.normalizeTag(next)}`, { type: 'success' });
    }

    async function deleteTag(name) {
        const ok = await ui.confirm({ title: `Remove #${name}?`, body: 'The tag is removed from every note. The notes themselves are kept.', okText: 'Remove tag', danger: true });
        if (!ok) return;
        await store.removeTag(name);
        if (store.state.view.type === 'tag') SN.app.go({ type: 'all' });
    }

    // ------------------------------------------------------------------ Context menus
    function noteMenu(id, pos) {
        const n = store.getNote(id);
        if (!n) return;
        const ids = [id];
        if (n.trashedAt) {
            ui.contextMenu(pos, [
                { label: 'Restore', icon: 'bi-arrow-counterclockwise', action: () => actions.restore(ids) },
                { label: 'Delete forever', icon: 'bi-x-octagon', danger: true, action: () => actions.deleteForever(ids) },
            ]);
            return;
        }
        const binders = store.listBinders();
        ui.contextMenu(pos, [
            { label: 'Open', icon: 'bi-box-arrow-up-right', action: () => SN.app.openNote(id, { focus: 'content' }) },
            '-',
            { label: n.pinned ? 'Unpin' : 'Pin', icon: n.pinned ? 'bi-pin-angle-fill' : 'bi-pin-angle', shortcut: `${APP_MOD}+P`, action: () => actions.pin(ids) },
            {
                label: 'Move to', icon: 'bi-folder-symlink', submenu: [
                    { label: 'Unfiled', icon: 'bi-inbox', checked: !n.binderId, action: () => actions.move(ids, null) },
                    ...binders.map((b) => ({ label: b.name, iconHtml: `<span class="${ui.colorClass(b.color)}" style="color:var(--c)">${ui.iconHtml(b.icon, 'bi-folder2')}</span>`, checked: n.binderId === b.id, action: () => actions.move(ids, b.id) })),
                    '-',
                    { label: 'New binder…', icon: 'bi-plus-lg', action: async () => { const b = await newBinder(); if (b) actions.move(ids, b.id); } },
                ],
            },
            { label: 'Colour & icon…', icon: 'bi-palette', action: () => actions.style(ids) },
            { label: 'Add tag…', icon: 'bi-tag', action: async () => { const t = await ui.prompt({ title: 'Add tag', label: 'Tag', placeholder: 'e.g. ideas', okText: 'Add' }); if (t) actions.addTag(ids, t); } },
            { label: 'Duplicate', icon: 'bi-copy', action: () => actions.duplicate(id) },
            { label: 'Download as Markdown', icon: 'bi-filetype-md', action: () => exportNote(id) },
            { label: 'Copy link', icon: 'bi-link-45deg', action: () => copyLink(id) },
            '-',
            { label: n.archived ? 'Unarchive' : 'Archive', icon: 'bi-archive', shortcut: `${APP_MOD}+A`, action: () => actions.archive(ids) },
            { label: 'Move to trash', icon: 'bi-trash3', danger: true, shortcut: `${APP_MOD}+Del`, action: () => actions.trash(ids) },
        ]);
    }

    function binderMenu(id, pos) {
        const b = store.getBinder(id);
        if (!b) return;
        ui.contextMenu(pos, [
            { header: b.name },
            { label: 'New note in binder', icon: 'bi-plus-lg', action: async () => { SN.app.go({ type: 'binder', id }); run('note.new'); } },
            { label: 'Edit binder…', icon: 'bi-pencil', action: () => editBinder(id) },
            '-',
            { label: 'Delete binder…', icon: 'bi-trash3', danger: true, action: () => deleteBinder(id) },
        ]);
    }

    function tagMenu(name, pos) {
        ui.contextMenu(pos, [
            { header: `#${name}` },
            { label: 'Rename tag…', icon: 'bi-pencil', action: () => renameTag(name) },
            { label: 'Remove tag from all notes…', icon: 'bi-x-circle', danger: true, action: () => deleteTag(name) },
        ]);
    }

    // ------------------------------------------------------------------ Single note helpers
    function exportNote(id) {
        const n = store.getNote(id);
        if (!n) return;
        const binder = n.binderId ? store.getBinder(n.binderId) : null;
        const text = util.buildFrontMatter(n, binder && binder.name) + (n.content || '');
        ui.download(`${util.safeFileName(n.title || util.deriveTitle(n.content), 'Untitled')}.md`, text, 'text/markdown;charset=utf-8');
    }

    async function copyLink(id) {
        if (await ui.copyText(`${location.origin}/#/note/${id}`)) ui.toast('Link copied', { type: 'success', timeout: 2000 });
    }

    function showInfo(id) {
        const n = store.getNote(id);
        if (!n) return;
        const binder = n.binderId ? store.getBinder(n.binderId) : null;
        const words = util.wordCount(util.stripMarkdown(n.content));
        const rows = [
            ['Title', n.title || 'Untitled'],
            ['Binder', binder ? binder.name : 'Unfiled'],
            ['Tags', (n.tags || []).map((t) => '#' + t).join(' ') || '—'],
            ['Created', util.formatDateTime(n.createdAt)],
            ['Last edited', util.formatDateTime(n.updatedAt)],
            ['Words', words.toLocaleString()],
            ['Characters', (n.content || '').length.toLocaleString()],
            ['Sync', n.dirty && n.dirty.length ? 'Changes waiting to sync' : n.rev ? `Synced (revision ${n.rev})` : 'Not synced yet'],
            ['ID', `<code class="small">${esc(n.id)}</code>`],
        ];
        $('#infoBody').html(rows.map(([k, v]) => `<dt class="col-4 text-body-secondary fw-normal">${esc(k)}</dt><dd class="col-8">${k === 'ID' ? v : esc(v)}</dd>`).join(''));
        ui.modal('infoModal').show();
    }

    function printNote() {
        const previous = SN.editor.mode();
        if (previous === 'edit') SN.editor.setMode('split');
        window.addEventListener('afterprint', () => SN.editor.setMode(previous), { once: true });
        setTimeout(() => window.print(), 50);
    }

    async function signOut() {
        await SN.editor.flush();
        let pending = await SN.sync.pendingCount();
        if (pending && navigator.onLine) {
            await SN.syncer.now();
            pending = await SN.sync.pendingCount();
        }
        const ok = await ui.confirm({
            title: 'Sign out?',
            body: pending
                ? `${plural(pending, 'change')} haven't synced yet and will be lost if you sign out now. Your notes will also be removed from this device.`
                : 'Your notes will be removed from this device (they stay safe on the server). You\'ll need your passkey to sign in again.',
            okText: pending ? 'Sign out anyway' : 'Sign out',
            danger: pending > 0,
        });
        if (!ok) return;
        let redirect = '/Account/Login?signedOut=true';
        try { redirect = (await ui.api('POST', '/api/logout')).redirect || redirect; } catch { /* offline: still clear locally */ }
        await SN.app.clearLocalData();
        location.assign(redirect);
    }

    // ------------------------------------------------------------------ Registry
    const noteOpen = () => !!currentNote();
    const sortCmd = (sort, title) => ({ id: `sort.${sort}`, group: 'View', title: `Sort by ${title}`, icon: 'bi-sort-down', run: () => store.setPrefs({ sort }) });
    const goCmd = (type, title, icon) => ({ id: `go.${type}`, group: 'Go to', title: `Go to ${title}`, icon, run: () => SN.app.go({ type }) });

    [
        { id: 'note.new', group: 'Notes', title: 'New note', icon: 'bi-plus-lg', keys: `${APP_MOD}+N`, run: () => SN.app.newNote() },
        { id: 'note.pin', group: 'Notes', title: 'Pin / unpin note', icon: 'bi-pin-angle', keys: `${APP_MOD}+P`, when: hasTarget, run: (c) => actions.pin(targetIds(c)) },
        { id: 'note.archive', group: 'Notes', title: 'Archive / unarchive note', icon: 'bi-archive', keys: `${APP_MOD}+A`, when: hasTarget, run: (c) => actions.archive(targetIds(c)) },
        { id: 'note.trash', group: 'Notes', title: 'Move note to trash', icon: 'bi-trash3', keys: `${APP_MOD}+Delete`, when: hasTarget, run: (c) => actions.trash(targetIds(c)) },
        { id: 'note.restore', group: 'Notes', title: 'Restore note from trash', icon: 'bi-arrow-counterclockwise', when: (c) => hasTarget(c) && store.getNote(targetIds(c)[0]).trashedAt, run: (c) => actions.restore(targetIds(c)) },
        { id: 'note.deleteForever', group: 'Notes', title: 'Delete note forever', icon: 'bi-x-octagon', when: hasTarget, run: (c) => actions.deleteForever(targetIds(c)) },
        { id: 'note.style', group: 'Notes', title: 'Change note colour & icon', icon: 'bi-palette', when: hasTarget, run: (c) => actions.style(targetIds(c)) },
        { id: 'note.duplicate', group: 'Notes', title: 'Duplicate note', icon: 'bi-copy', when: hasTarget, run: (c) => actions.duplicate(targetIds(c)[0]) },
        { id: 'note.export', group: 'Notes', title: 'Download note as Markdown', icon: 'bi-filetype-md', when: hasTarget, run: (c) => exportNote(targetIds(c)[0]) },
        { id: 'note.copyMarkdown', group: 'Notes', title: 'Copy note as Markdown', icon: 'bi-clipboard', when: noteOpen, run: async () => { if (await ui.copyText(currentNote().content || '')) ui.toast('Markdown copied', { type: 'success', timeout: 2000 }); } },
        { id: 'note.copyLink', group: 'Notes', title: 'Copy link to note', icon: 'bi-link-45deg', when: hasTarget, run: (c) => copyLink(targetIds(c)[0]) },
        { id: 'note.print', group: 'Notes', title: 'Print note', icon: 'bi-printer', when: noteOpen, run: printNote },
        { id: 'note.info', group: 'Notes', title: 'Note info', icon: 'bi-info-circle', when: noteOpen, run: () => showInfo(currentNote().id) },
        { id: 'note.close', group: 'Notes', title: 'Close note', icon: 'bi-x-lg', when: noteOpen, run: () => SN.app.closeNote() },
        { id: 'note.next', group: 'Notes', title: 'Next note', icon: 'bi-arrow-down', keys: `${APP_MOD}+↓`, run: () => SN.app.stepNote(1) },
        { id: 'note.prev', group: 'Notes', title: 'Previous note', icon: 'bi-arrow-up', keys: `${APP_MOD}+↑`, run: () => SN.app.stepNote(-1) },
        { id: 'note.addTag', group: 'Notes', title: 'Add tag to note', icon: 'bi-tag', when: noteOpen, run: () => $('#tagInput').trigger('focus') },
        { id: 'notes.import', group: 'Notes', title: 'Import Markdown files', icon: 'bi-file-earmark-arrow-up', run: () => $('#importFileInput').val('').trigger('click') },
        { id: 'notes.exportAll', group: 'Notes', title: 'Export all notes (.zip)', icon: 'bi-file-earmark-zip', run: () => { location.href = '/api/export'; } },
        { id: 'trash.empty', group: 'Notes', title: 'Empty trash', icon: 'bi-trash3', run: () => actions.emptyTrash() },
        { id: 'actions.undo', group: 'Notes', title: 'Undo last note action', icon: 'bi-arrow-counterclockwise', keys: 'Ctrl+Z', run: undoAction },
        { id: 'actions.redo', group: 'Notes', title: 'Redo note action', icon: 'bi-arrow-clockwise', keys: 'Ctrl+Y', run: redoAction },

        { id: 'binder.new', group: 'Binders', title: 'New binder', icon: 'bi-folder-plus', run: (c) => newBinder(c) },
        { id: 'binder.edit', group: 'Binders', title: 'Edit current binder', icon: 'bi-pencil', when: () => store.state.view.type === 'binder', run: () => editBinder(store.state.view.id) },
        { id: 'binder.delete', group: 'Binders', title: 'Delete current binder', icon: 'bi-trash3', when: () => store.state.view.type === 'binder', run: () => deleteBinder(store.state.view.id) },

        goCmd('all', 'all notes', 'bi-journal-text'), goCmd('pinned', 'pinned notes', 'bi-pin-angle'),
        goCmd('unfiled', 'unfiled notes', 'bi-inbox'), goCmd('archive', 'archive', 'bi-archive'), goCmd('trash', 'trash', 'bi-trash3'),
        { id: 'search.focus', group: 'Go to', title: 'Search notes', icon: 'bi-search', keys: '/', run: () => SN.app.focusSearch() },
        { id: 'palette.open', group: 'Go to', title: 'Command palette', icon: 'bi-command', keys: 'Ctrl+K', run: () => palette.open('') },
        { id: 'palette.commands', group: 'Go to', title: 'Show all commands', icon: 'bi-terminal', keys: 'Ctrl+Shift+P', run: () => palette.open('>') },

        { id: 'view.toggle', group: 'View', title: 'Switch between list and cards', icon: 'bi-grid-3x2-gap', keys: `${APP_MOD}+V`, run: () => store.setPrefs({ viewMode: store.state.prefs.viewMode === 'grid' ? 'list' : 'grid' }) },
        { id: 'view.list', group: 'View', title: 'Show notes as a list', icon: 'bi-list-ul', run: () => store.setPrefs({ viewMode: 'list' }) },
        { id: 'view.grid', group: 'View', title: 'Show notes as cards', icon: 'bi-grid-3x2-gap', run: () => store.setPrefs({ viewMode: 'grid' }) },
        sortCmd('updated', 'date modified'), sortCmd('created', 'date created'), sortCmd('title', 'title'), sortCmd('manual', 'custom order (drag & drop)'),
        { id: 'sort.reverse', group: 'View', title: 'Reverse sort order', icon: 'bi-arrow-down-up', run: () => store.setPrefs({ sortDir: store.state.prefs.sortDir === 'asc' ? 'desc' : 'asc' }) },
        { id: 'editor.mode.cycle', group: 'View', title: 'Switch editor / split / preview', icon: 'bi-layout-split', keys: 'Ctrl+E', when: noteOpen, run: () => SN.editor.cycleMode() },
        { id: 'editor.mode.edit', group: 'View', title: 'Editor only', icon: 'bi-pencil', run: () => SN.editor.setMode('edit', true) },
        { id: 'editor.mode.split', group: 'View', title: 'Editor and preview side by side', icon: 'bi-layout-split', run: () => SN.editor.setMode('split', true) },
        { id: 'editor.mode.preview', group: 'View', title: 'Preview only', icon: 'bi-eye', run: () => SN.editor.setMode('preview', true) },
        { id: 'sidebar.toggle', group: 'View', title: 'Show / hide sidebar', icon: 'bi-layout-sidebar', keys: 'Ctrl+\\', run: () => SN.app.toggleSidebar() },
        { id: 'focus.toggle', group: 'View', title: 'Focus mode (hide sidebar and list)', icon: 'bi-arrows-fullscreen', keys: 'Ctrl+Shift+\\', run: () => document.body.classList.toggle('focus-mode') },
        { id: 'theme.cycle', group: 'View', title: 'Cycle theme (system / light / dark)', icon: 'bi-circle-half', keys: `${APP_MOD}+T`, run: () => SN.app.setTheme(window.SNTheme.cycle()) },
        { id: 'theme.light', group: 'View', title: 'Use light theme', icon: 'bi-sun', run: () => SN.app.setTheme('light') },
        { id: 'theme.dark', group: 'View', title: 'Use dark theme', icon: 'bi-moon-stars', run: () => SN.app.setTheme('dark') },
        { id: 'theme.auto', group: 'View', title: 'Use system theme', icon: 'bi-circle-half', run: () => SN.app.setTheme('auto') },

        { id: 'sync.now', group: 'App', title: 'Save & sync now', icon: 'bi-arrow-repeat', keys: 'Ctrl+S', run: async () => { await SN.editor.flush(); await SN.syncer.now(); if (SN.syncer.status === 'synced') ui.toast('All changes synced', { type: 'success', timeout: 1800 }); else if (SN.syncer.status === 'offline') ui.toast('You\'re offline – changes are saved on this device and will sync later.', { type: 'warning' }); } },
        { id: 'app.lock', group: 'App', title: 'Lock app', icon: 'bi-lock', keys: 'Ctrl+Shift+L', run: () => SN.lock.lock({ reason: 'manual' }) },
        { id: 'settings.open', group: 'App', title: 'Settings', icon: 'bi-gear', keys: 'Ctrl+,', run: (c) => SN.settings.open(c.tab) },
        { id: 'settings.security', group: 'App', title: 'Manage passkeys & app lock', icon: 'bi-shield-lock', run: () => SN.settings.open('security') },
        { id: 'settings.integrations', group: 'App', title: 'PowerToys & integrations', icon: 'bi-plug', run: () => SN.settings.open('integrations') },
        { id: 'help.shortcuts', group: 'App', title: 'Keyboard shortcuts', icon: 'bi-keyboard', keys: '?', run: showShortcuts },
        { id: 'app.install', group: 'App', title: 'Install SyncedNotes as an app', icon: 'bi-download', when: () => SN.pwa && SN.pwa.canInstall(), run: () => SN.pwa.install() },
        { id: 'account.signout', group: 'App', title: 'Sign out', icon: 'bi-box-arrow-right', run: signOut },
    ].forEach(register);

    // Editor-only shortcuts shown in the help.
    const EDITOR_KEYS = [
        ['Bold', 'Ctrl+B'], ['Italic', 'Ctrl+I'], ['Strikethrough', 'Ctrl+Shift+X'], ['Inline code', 'Ctrl+`'],
        ['Link', 'Ctrl+K'], ['Numbered list', 'Ctrl+Shift+7'], ['Bulleted list', 'Ctrl+Shift+8'], ['Checklist', 'Ctrl+Shift+9'],
        ['Undo / redo', 'Ctrl+Z / Ctrl+Y'], ['Indent / outdent', 'Tab / Shift+Tab'], ['Leave the editor', 'Esc'],
    ];

    function showShortcuts() {
        const groups = {};
        for (const cmd of registry.values()) {
            if (!cmd.keys) continue;
            (groups[cmd.group] = groups[cmd.group] || []).push([cmd.title, cmd.keys]);
        }
        groups['Writing'] = EDITOR_KEYS;
        groups['Note list'] = [['Move between notes', '↑ / ↓'], ['Open note in editor', 'Enter'], ['Note actions', 'Shift+F10 / right-click'], ['Move to trash', 'Delete']];
        const kbd = (keys) => keys.split(' / ').map((k) => k.split('+').map((p) => `<kbd>${esc(ui.formatShortcut(p))}</kbd>`).join(' ')).join(' / ');
        $('#shortcutsBody').html(Object.entries(groups).map(([g, rows]) =>
            `<h4>${esc(g)}</h4>${rows.map(([t, k]) => `<div class="shortcut-row"><span>${esc(t)}</span><span>${kbd(k)}</span></div>`).join('')}`).join(''));
        ui.modal('shortcutsModal').show();
    }

    // ------------------------------------------------------------------ Command palette
    const palette = {
        results: [],
        active: 0,
        open(prefix) {
            const $input = $('#paletteInput');
            $input.val(prefix || '');
            this.render();
            const el = document.getElementById('paletteModal');
            const m = bootstrap.Modal.getOrCreateInstance(el);
            $(el).one('shown.bs.modal', () => { $input.trigger('focus'); const v = $input.val(); $input[0].setSelectionRange(v.length, v.length); });
            m.show();
        },
        close() { const m = bootstrap.Modal.getInstance(document.getElementById('paletteModal')); if (m) m.hide(); },
        build(q) {
            let mode = 'all', term = q;
            if (q.startsWith('>')) { mode = 'commands'; term = q.slice(1); }
            else if (q.startsWith('#')) { mode = 'tags'; term = q.slice(1); }
            else if (q.startsWith('@')) { mode = 'binders'; term = q.slice(1); }
            term = term.trim();
            const out = [];

            if (mode === 'tags') {
                store.allTags().map((t) => ({ t, s: util.fuzzyScore(term, t.name) })).filter((x) => x.s >= 0).sort((a, b) => b.s - a.s).slice(0, 30)
                    .forEach(({ t }) => out.push({ group: 'Tags', icon: '<i class="bi bi-hash"></i>', title: t.name, sub: plural(t.count, 'note'), run: () => SN.app.go({ type: 'tag', id: t.name }) }));
                return out;
            }
            if (mode === 'binders') {
                const n = currentNote();
                store.listBinders().map((b) => ({ b, s: util.fuzzyScore(term, b.name) })).filter((x) => x.s >= 0).sort((a, b) => b.s - a.s)
                    .forEach(({ b }) => {
                        out.push({ group: 'Binders', icon: `<span class="${ui.colorClass(b.color)}">${ui.iconHtml(b.icon, 'bi-folder2')}</span>`, title: b.name, sub: 'Open binder', run: () => SN.app.go({ type: 'binder', id: b.id }) });
                        if (n) out.push({ group: 'Move current note', icon: '<i class="bi bi-folder-symlink"></i>', title: `Move “${n.title || 'Untitled'}” to ${b.name}`, run: () => actions.move([n.id], b.id) });
                    });
                return out;
            }

            if (mode === 'all') {
                const notes = Array.from(store.state.notes.values()).filter((n) => !n.deleted && !n.trashedAt);
                let matches;
                if (!term) {
                    matches = notes.sort((a, b) => b.updatedAt - a.updatedAt).slice(0, 7).map((n) => ({ n }));
                } else {
                    const lower = term.toLowerCase();
                    matches = notes.map((n) => {
                        let s = util.fuzzyScore(term, n.title || util.deriveTitle(n.content));
                        if (s < 0 && (n.content || '').toLowerCase().includes(lower)) s = 1;
                        return { n, s };
                    }).filter((x) => x.s >= 0).sort((a, b) => b.s - a.s || b.n.updatedAt - a.n.updatedAt).slice(0, 12);
                }
                matches.forEach(({ n }) => {
                    const binder = n.binderId ? store.getBinder(n.binderId) : null;
                    out.push({
                        group: term ? 'Notes' : 'Recent notes',
                        icon: `<span class="${ui.colorClass(n.color)}">${ui.iconHtml(n.icon, 'bi-file-earmark-text')}</span>`,
                        title: n.title || util.deriveTitle(n.content) || 'Untitled',
                        sub: [binder ? binder.name : null, n.archived ? 'Archived' : null, util.formatRelative(n.updatedAt)].filter(Boolean).join(' · '),
                        run: () => SN.app.openNote(n.id, { focus: 'content' }),
                    });
                });
            }

            const cmds = Array.from(registry.values()).filter((c) => !c.when || c.when({}))
                .map((c) => ({ c, s: term ? util.fuzzyScore(term, `${c.title} ${c.group}`) : 0 }))
                .filter((x) => x.s >= 0)
                .sort((a, b) => b.s - a.s);
            const limit = mode === 'commands' ? 100 : term ? 8 : 6;
            const featured = ['note.new', 'search.focus', 'view.toggle', 'theme.cycle', 'sync.now', 'app.lock', 'settings.open'];
            (mode === 'all' && !term ? cmds.filter((x) => featured.includes(x.c.id)) : cmds).slice(0, limit).forEach(({ c }) => {
                out.push({ group: 'Commands', icon: `<i class="bi ${c.icon || 'bi-dot'}"></i>`, title: c.title, shortcut: c.keys, run: () => run(c.id) });
            });

            if (mode === 'all' && term && !store.findNoteByTitle(term)) {
                out.push({ group: 'Create', icon: '<i class="bi bi-plus-lg"></i>', title: `Create note “${term}”`, run: () => SN.app.newNote({ title: term }) });
            }
            return out;
        },
        render() {
            this.results = this.build(String($('#paletteInput').val() || ''));
            this.active = 0;
            const $list = $('#paletteList');
            if (!this.results.length) { $list.html('<li class="palette-empty">No results</li>'); return; }
            let group = null;
            const html = [];
            this.results.forEach((r, i) => {
                if (r.group !== group) { group = r.group; html.push(`<li class="palette-group" role="presentation">${esc(group)}</li>`); }
                html.push(`<li class="palette-item${i === 0 ? ' active' : ''}" role="option" id="pi-${i}" data-index="${i}" aria-selected="${i === 0}">
                    ${r.icon}<div class="pi-text"><div class="pi-title">${esc(r.title)}</div>${r.sub ? `<div class="pi-sub">${esc(r.sub)}</div>` : ''}</div>
                    ${r.shortcut ? `<span class="small text-body-secondary">${r.shortcut.split('+').map((k) => `<kbd>${esc(ui.formatShortcut(k))}</kbd>`).join(' ')}</span>` : ''}</li>`);
            });
            $list.html(html.join(''));
            $('#paletteInput').attr('aria-activedescendant', 'pi-0');
        },
        setActive(i) {
            if (!this.results.length) return;
            this.active = (i + this.results.length) % this.results.length;
            const $items = $('#paletteList .palette-item');
            $items.removeClass('active').attr('aria-selected', 'false');
            const $a = $items.filter(`[data-index=${this.active}]`).addClass('active').attr('aria-selected', 'true');
            if ($a[0]) $a[0].scrollIntoView({ block: 'nearest' });
            $('#paletteInput').attr('aria-activedescendant', `pi-${this.active}`);
        },
        execute(i) {
            const r = this.results[i];
            if (!r) return;
            this.close();
            setTimeout(() => r.run(), 60);
        },
    };

    function bindPalette() {
        $('#paletteInput').on('input', () => palette.render()).on('keydown', (e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); palette.setActive(palette.active + 1); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); palette.setActive(palette.active - 1); }
            else if (e.key === 'Enter') { e.preventDefault(); palette.execute(palette.active); }
            else if (e.key === 'PageDown') { e.preventDefault(); palette.setActive(palette.active + 8); }
            else if (e.key === 'PageUp') { e.preventDefault(); palette.setActive(palette.active - 8); }
        });
        $('#paletteList').on('mousemove', '.palette-item', function () {
            const i = Number(this.dataset.index);
            if (i !== palette.active) palette.setActive(i);
        }).on('click', '.palette-item', function () { palette.execute(Number(this.dataset.index)); });
    }

    // ------------------------------------------------------------------ Keyboard shortcuts
    function bindKeys() {
        document.addEventListener('keydown', (e) => {
            if (e.defaultPrevented || (SN.lock && SN.lock.isLocked())) return;
            const code = e.code;
            const mod = e.ctrlKey || e.metaKey;
            const altGr = e.getModifierState && e.getModifierState('AltGraph');
            const appMod = !altGr && (isMac ? e.ctrlKey && e.altKey && !e.metaKey : e.altKey && !e.ctrlKey && !e.metaKey);
            const typing = ui.isTyping(e.target);
            const modalOpen = !!document.querySelector('.modal.show');
            const paletteOpen = $('#paletteModal').hasClass('show');

            const trigger = (id) => { e.preventDefault(); run(id); };

            if (mod && !e.altKey && !e.shiftKey && code === 'KeyK') { e.preventDefault(); if (paletteOpen) palette.close(); else if (!modalOpen) palette.open(''); return; }
            if (mod && !e.altKey && code === 'KeyP') { e.preventDefault(); if (!modalOpen) palette.open(e.shiftKey ? '>' : ''); return; }
            // Locking must work from anywhere, even with a dialog open.
            if (mod && !e.altKey && e.shiftKey && code === 'KeyL') return trigger('app.lock');
            if (modalOpen) return;

            if (mod && !e.altKey && !e.shiftKey && code === 'KeyS') return trigger('sync.now');
            if (mod && !e.altKey && !e.shiftKey && code === 'Comma') return trigger('settings.open');
            if (mod && !e.altKey && !e.shiftKey && code === 'KeyE') return trigger('editor.mode.cycle');
            if (mod && !e.altKey && code === 'Backslash') return trigger(e.shiftKey ? 'focus.toggle' : 'sidebar.toggle');
            if (mod && !e.altKey && e.shiftKey && code === 'KeyF') return trigger('search.focus');
            if (appMod && !e.shiftKey) {
                if (code === 'KeyN') return trigger('note.new');
                if (code === 'KeyP') return trigger('note.pin');
                if (code === 'KeyA') return trigger('note.archive');
                if (code === 'Delete' || code === 'Backspace') return trigger('note.trash');
                if (code === 'KeyV') return trigger('view.toggle');
                if (code === 'KeyT') return trigger('theme.cycle');
                if (code === 'ArrowDown') return trigger('note.next');
                if (code === 'ArrowUp') return trigger('note.prev');
            }

            if (typing) return;
            if (mod && !e.altKey && code === 'KeyZ') { e.preventDefault(); if (e.shiftKey) redoAction(); else undoAction(); return; }
            if (mod && !e.altKey && !e.shiftKey && code === 'KeyY') { e.preventDefault(); redoAction(); return; }
            if (!mod && !e.altKey && e.key === '/') return trigger('search.focus');
            if (!mod && !e.altKey && e.key === '?') return trigger('help.shortcuts');
            if (e.key === 'Escape' && document.body.classList.contains('focus-mode')) { document.body.classList.remove('focus-mode'); return; }
            if (e.key === 'Escape' && SN.editor.currentId() && window.matchMedia('(max-width: 767.98px)').matches) return trigger('note.close');
        });
    }

    // ------------------------------------------------------------------ Global click delegation
    function bindClicks() {
        $(document).on('click', '[data-cmd]', function (e) {
            e.preventDefault();
            run(String($(this).data('cmd')), { source: this });
        });
        $('#sortMenu').on('click', '[data-sort]', function () { store.setPrefs({ sort: String($(this).data('sort')) }); })
            .on('click', '[data-sortdir]', function () { store.setPrefs({ sortDir: String($(this).data('sortdir')) }); })
            .on('click', '[data-viewmode]', function () { store.setPrefs({ viewMode: String($(this).data('viewmode')) }); });
        $('#sortMenu').closest('.dropdown').on('show.bs.dropdown', () => {
            const p = store.state.prefs;
            $('#sortMenu [data-sort]').each(function () { $(this).toggleClass('checked', $(this).data('sort') === p.sort); });
            $('#sortMenu [data-sortdir]').each(function () { $(this).toggleClass('checked', $(this).data('sortdir') === p.sortDir).closest('li').prop('hidden', p.sort === 'manual'); });
            $('#sortMenu [data-viewmode]').each(function () { $(this).toggleClass('checked', $(this).data('viewmode') === p.viewMode); });
            const labels = p.sort === 'title' ? ['Z → A', 'A → Z'] : ['Newest first', 'Oldest first'];
            $('#sortMenu [data-dir-label=desc]').text(labels[0]);
            $('#sortMenu [data-dir-label=asc]').text(labels[1]);
        });
    }

    function init() {
        bindPalette();
        bindKeys();
        bindClicks();
    }

    SN.commands = {
        init, register, run, actions, dropNote, reorder, noteMenu, binderMenu, tagMenu,
        undoAction, redoAction, palette, showShortcuts,
    };
})(jQuery);
