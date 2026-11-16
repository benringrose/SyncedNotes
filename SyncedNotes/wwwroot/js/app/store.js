/* SyncedNotes – application state over the offline store (IndexedDB), plus the sync scheduler. */
(function ($) {
    'use strict';
    const { util, db } = SN;

    const DEFAULT_PREFS = {
        theme: 'auto', viewMode: 'list', sort: 'updated', sortDir: 'desc', showSnippets: true,
        editorMode: 'split', font: 'sans', fontSize: 15, spellcheck: true, toolbar: true, syncScroll: true,
    };
    const NOTE_FIELDS = ['binderId', 'title', 'content', 'color', 'icon', 'tags', 'pinned', 'archived', 'trashedAt', 'sortOrder'];
    const BINDER_FIELDS = ['name', 'color', 'icon', 'sortOrder'];

    const state = {
        notes: new Map(),
        binders: new Map(),
        prefs: { ...DEFAULT_PREFS },
        view: { type: 'all', id: null },
        search: '',
        selectedId: null,
    };

    // ------------------------------------------------------------------ Events
    const bus = $({});
    const on = (event, fn) => bus.on(event, (e, data) => fn(data));
    const emit = (event, data) => bus.trigger(event, [data]);

    const channel = 'BroadcastChannel' in window ? new BroadcastChannel('syncednotes') : null;
    function broadcast(message) { try { channel && channel.postMessage(message); } catch { /* closed */ } }

    const equal = (a, b) => (Array.isArray(a) || Array.isArray(b) ? JSON.stringify(a || []) === JSON.stringify(b || []) : (a ?? null) === (b ?? null));
    const visible = (r) => r && !r.deleted;

    // ------------------------------------------------------------------ Loading
    async function load() {
        const [notes, binders, prefs] = await Promise.all([db.getAll('notes'), db.getAll('binders'), db.getMeta('prefs')]);
        state.notes = new Map(notes.map((n) => [n.id, n]));
        state.binders = new Map(binders.map((b) => [b.id, b]));
        state.prefs = { ...DEFAULT_PREFS, ...(prefs && prefs.data) };
    }

    /** Re-reads IndexedDB (after a sync or a change in another tab) and reports what changed. */
    async function reloadFromDb(source) {
        const [notes, binders, prefs] = await Promise.all([db.getAll('notes'), db.getAll('binders'), db.getMeta('prefs')]);
        const changedNotes = [], textChanged = [];
        const next = new Map(notes.map((n) => [n.id, n]));
        for (const [id, n] of next) {
            const old = state.notes.get(id);
            if (!old || old.rev !== n.rev || old.lv !== n.lv || old.deleted !== n.deleted || JSON.stringify(old.dirty) !== JSON.stringify(n.dirty)) {
                changedNotes.push(id);
                if (old && (old.content !== n.content || old.title !== n.title)) textChanged.push(id);
            }
        }
        for (const id of state.notes.keys()) if (!next.has(id)) changedNotes.push(id);
        state.notes = next;

        const nextBinders = new Map(binders.map((b) => [b.id, b]));
        const binderChanged = binders.length !== state.binders.size || binders.some((b) => {
            const old = state.binders.get(b.id);
            return !old || old.rev !== b.rev || old.lv !== b.lv || old.deleted !== b.deleted;
        });
        state.binders = nextBinders;

        const newPrefs = { ...DEFAULT_PREFS, ...(prefs && prefs.data) };
        const prefsChanged = JSON.stringify(newPrefs) !== JSON.stringify(state.prefs);
        state.prefs = newPrefs;

        if (changedNotes.length) emit('notes', { ids: changedNotes, textChanged, source: source || 'remote' });
        if (binderChanged) emit('binders', { source: source || 'remote' });
        if (prefsChanged) emit('prefs', { source: source || 'remote' });
    }

    // ------------------------------------------------------------------ Queries
    const getNote = (id) => { const n = state.notes.get(id); return visible(n) ? n : null; };
    const getBinder = (id) => { const b = state.binders.get(id); return visible(b) ? b : null; };

    function listBinders() {
        return Array.from(state.binders.values()).filter(visible)
            .sort((a, b) => (a.sortOrder - b.sortOrder) || a.name.localeCompare(b.name));
    }

    function inView(n, view) {
        if (!visible(n)) return false;
        if (view.type === 'trash') return !!n.trashedAt;
        if (n.trashedAt) return false;
        if (view.type === 'archive') return !!n.archived;
        if (n.archived) return false;
        switch (view.type) {
            case 'pinned': return !!n.pinned;
            case 'unfiled': return !n.binderId || !getBinder(n.binderId);
            case 'binder': return n.binderId === view.id;
            case 'tag': return (n.tags || []).some((t) => t.toLowerCase() === String(view.id).toLowerCase());
            default: return true;
        }
    }

    /** Parses the search box: words match title/content/tags, #tag filters by tag. */
    function parseSearch(q) {
        const tags = [], words = [];
        String(q || '').toLowerCase().split(/\s+/).filter(Boolean).forEach((t) => {
            if (t.startsWith('#') && t.length > 1) tags.push(t.slice(1)); else words.push(t);
        });
        return { tags, words };
    }

    function matchesSearch(n, parsed) {
        if (!parsed.tags.length && !parsed.words.length) return true;
        const noteTags = (n.tags || []).map((t) => t.toLowerCase());
        if (!parsed.tags.every((t) => noteTags.some((nt) => nt.startsWith(t)))) return false;
        if (!parsed.words.length) return true;
        const hay = `${n.title || ''}\n${n.content || ''}\n${noteTags.join(' ')}`.toLowerCase();
        return parsed.words.every((w) => hay.includes(w));
    }

    function comparator(prefs) {
        const dir = prefs.sortDir === 'asc' ? 1 : -1;
        switch (prefs.sort) {
            case 'title': return (a, b) => dir * (a.title || 'Untitled').localeCompare(b.title || 'Untitled', undefined, { sensitivity: 'base', numeric: true }) || (b.updatedAt - a.updatedAt);
            case 'created': return (a, b) => dir * (a.createdAt - b.createdAt);
            case 'manual': return (a, b) => (a.sortOrder - b.sortOrder) || (b.updatedAt - a.updatedAt);
            default: return (a, b) => dir * (a.updatedAt - b.updatedAt);
        }
    }

    /** Notes for the current view and search, split into pinned and other notes. */
    function visibleNotes() {
        const parsed = parseSearch(state.search);
        const list = Array.from(state.notes.values()).filter((n) => inView(n, state.view) && matchesSearch(n, parsed));
        const sorted = list.sort(state.view.type === 'trash' ? (a, b) => b.trashedAt - a.trashedAt : comparator(state.prefs));
        const splitPins = !['trash', 'archive', 'pinned'].includes(state.view.type);
        return {
            pinned: splitPins ? sorted.filter((n) => n.pinned) : [],
            others: splitPins ? sorted.filter((n) => !n.pinned) : sorted,
            total: sorted.length,
        };
    }

    function counts() {
        const c = { all: 0, pinned: 0, unfiled: 0, archive: 0, trash: 0, binders: {}, tags: {} };
        for (const n of state.notes.values()) {
            if (!visible(n)) continue;
            if (n.trashedAt) { c.trash++; continue; }
            if (n.archived) { c.archive++; continue; }
            c.all++;
            if (n.pinned) c.pinned++;
            if (n.binderId && getBinder(n.binderId)) c.binders[n.binderId] = (c.binders[n.binderId] || 0) + 1; else c.unfiled++;
            for (const t of n.tags || []) {
                const key = t.toLowerCase();
                if (!c.tags[key]) c.tags[key] = { name: t, count: 0 };
                c.tags[key].count++;
            }
        }
        return c;
    }

    function allTags() {
        const map = new Map();
        for (const n of state.notes.values()) {
            if (!visible(n) || n.trashedAt) continue;
            for (const t of n.tags || []) {
                const key = t.toLowerCase();
                const entry = map.get(key) || { name: t, count: 0 };
                entry.count++;
                map.set(key, entry);
            }
        }
        return Array.from(map.values()).sort((a, b) => a.name.localeCompare(b.name));
    }

    function findNoteByTitle(title) {
        const t = String(title || '').trim().toLowerCase();
        if (!t) return null;
        for (const n of state.notes.values()) {
            if (visible(n) && !n.trashedAt && (n.title || '').trim().toLowerCase() === t) return n;
        }
        return null;
    }

    function minSortOrder(map) {
        let min = Infinity;
        for (const r of map.values()) if (visible(r) && typeof r.sortOrder === 'number' && r.sortOrder < min) min = r.sortOrder;
        return min === Infinity ? 0 : min;
    }

    function maxSortOrder(map) {
        let max = -Infinity;
        for (const r of map.values()) if (visible(r) && typeof r.sortOrder === 'number' && r.sortOrder > max) max = r.sortOrder;
        return max === -Infinity ? 0 : max;
    }

    // ------------------------------------------------------------------ Writes
    /**
     * Applies patches atomically on top of the latest stored records (so concurrent sync updates such as
     * new revisions are never lost), records which fields changed, and bumps the local version.
     */
    async function patchRecords(storeName, fields, patches, source) {
        const results = [];
        const now = Date.now();
        await db.tx(storeName, 'readwrite', (s) => {
            for (const [id, patch] of patches) {
                const req = s[storeName].get(id);
                req.onsuccess = () => {
                    const rec = req.result;
                    if (!rec) return;
                    const changed = [];
                    for (const f of fields) {
                        if (Object.prototype.hasOwnProperty.call(patch, f) && !equal(rec[f], patch[f])) {
                            rec[f] = Array.isArray(patch[f]) ? patch[f].slice() : patch[f];
                            changed.push(f);
                        }
                    }
                    if (!changed.length) { results.push(rec); return; }
                    if (storeName === 'notes' && (changed.includes('title') || changed.includes('content'))) rec.updatedAt = patch.updatedAt || now;
                    if (storeName === 'binders') rec.updatedAt = now;
                    rec.dirty = Array.from(new Set([...(rec.dirty || []), ...changed]));
                    rec.lv = (rec.lv || 0) + 1;
                    s[storeName].put(rec);
                    results.push(rec);
                };
            }
        });
        const map = storeName === 'notes' ? state.notes : state.binders;
        results.forEach((r) => map.set(r.id, r));
        const ids = results.map((r) => r.id);
        if (storeName === 'notes') emit('notes', { ids, source: source || 'local' });
        else emit('binders', { source: source || 'local' });
        broadcast({ type: 'changed' });
        syncer.request();
        return results;
    }

    async function insertRecords(storeName, records) {
        await db.putMany(storeName, records);
        const map = storeName === 'notes' ? state.notes : state.binders;
        records.forEach((r) => map.set(r.id, r));
        if (storeName === 'notes') emit('notes', { ids: records.map((r) => r.id), source: 'local' });
        else emit('binders', { source: 'local' });
        broadcast({ type: 'changed' });
        syncer.request();
        return records;
    }

    function newNoteRecord(init) {
        const now = Date.now();
        return {
            id: util.uuid(),
            binderId: init.binderId || null,
            title: init.title || '',
            content: init.content || '',
            color: init.color || null,
            icon: init.icon || null,
            tags: init.tags || [],
            pinned: !!init.pinned,
            archived: !!init.archived,
            trashedAt: null,
            sortOrder: typeof init.sortOrder === 'number' ? init.sortOrder : minSortOrder(state.notes) - 1,
            createdAt: init.createdAt || now,
            updatedAt: init.updatedAt || now,
            rev: 0,
            deleted: false,
            dirty: ['*'],
            lv: 1,
        };
    }

    /** Creates a note, inheriting the binder/tag/pinned state of the current view. */
    async function createNote(init) {
        init = init || {};
        const view = state.view;
        const record = newNoteRecord({
            binderId: view.type === 'binder' ? view.id : null,
            tags: view.type === 'tag' ? [view.id] : [],
            pinned: view.type === 'pinned',
            ...init,
        });
        await insertRecords('notes', [record]);
        return record;
    }

    const updateNote = (id, patch, source) => patchRecords('notes', NOTE_FIELDS, new Map([[id, patch]]), source).then((r) => r[0]);

    function updateNotes(ids, patchFn) {
        const patches = new Map();
        ids.forEach((id) => { const n = state.notes.get(id); if (n) patches.set(id, typeof patchFn === 'function' ? patchFn(n) : patchFn); });
        return patchRecords('notes', NOTE_FIELDS, patches);
    }

    /** Permanently deletes notes. Never-synced notes are simply removed; others become tombstones to push. */
    async function deleteNotesForever(ids) {
        const removeIds = [];
        await db.tx('notes', 'readwrite', (s) => {
            ids.forEach((id) => {
                const req = s.notes.get(id);
                req.onsuccess = () => {
                    const rec = req.result;
                    if (!rec) return;
                    if (!rec.rev) { s.notes.delete(id); removeIds.push(id); return; }
                    rec.deleted = true;
                    rec.dirty = Array.from(new Set([...(rec.dirty || []), 'deleted']));
                    rec.lv = (rec.lv || 0) + 1;
                    s.notes.put(rec);
                    state.notes.set(id, rec);
                };
            });
        });
        removeIds.forEach((id) => state.notes.delete(id));
        emit('notes', { ids, source: 'local' });
        broadcast({ type: 'changed' });
        syncer.request();
    }

    async function createBinder(init) {
        const now = Date.now();
        const record = {
            id: util.uuid(), name: init.name, color: init.color || null, icon: init.icon || null,
            sortOrder: maxSortOrder(state.binders) + 1, createdAt: now, updatedAt: now,
            rev: 0, deleted: false, dirty: ['*'], lv: 1,
        };
        await insertRecords('binders', [record]);
        return record;
    }

    const updateBinder = (id, patch) => patchRecords('binders', BINDER_FIELDS, new Map([[id, patch]])).then((r) => r[0]);
    const updateBinders = (patches) => patchRecords('binders', BINDER_FIELDS, patches);

    async function deleteBinder(id, opts) {
        const noteIds = Array.from(state.notes.values()).filter((n) => visible(n) && n.binderId === id).map((n) => n.id);
        if (noteIds.length) {
            const now = Date.now();
            await updateNotes(noteIds, () => (opts && opts.trashNotes ? { binderId: null, trashedAt: now, pinned: false } : { binderId: null }));
        }
        const rec = await db.get('binders', id);
        if (!rec) return;
        if (!rec.rev) { await db.delete('binders', id); state.binders.delete(id); }
        else {
            rec.deleted = true;
            rec.dirty = Array.from(new Set([...(rec.dirty || []), 'deleted']));
            rec.lv = (rec.lv || 0) + 1;
            await db.put('binders', rec);
            state.binders.set(id, rec);
        }
        emit('binders', { source: 'local' });
        broadcast({ type: 'changed' });
        syncer.request();
    }

    function renameTag(oldName, newName) {
        const from = oldName.toLowerCase();
        const to = util.normalizeTag(newName);
        if (!to) return Promise.resolve([]);
        const ids = Array.from(state.notes.values()).filter((n) => visible(n) && (n.tags || []).some((t) => t.toLowerCase() === from)).map((n) => n.id);
        return updateNotes(ids, (n) => {
            const tags = [];
            for (const t of n.tags) {
                const v = t.toLowerCase() === from ? to : t;
                if (!tags.some((x) => x.toLowerCase() === v.toLowerCase())) tags.push(v);
            }
            return { tags };
        });
    }

    function removeTag(name) {
        const key = name.toLowerCase();
        const ids = Array.from(state.notes.values()).filter((n) => visible(n) && (n.tags || []).some((t) => t.toLowerCase() === key)).map((n) => n.id);
        return updateNotes(ids, (n) => ({ tags: n.tags.filter((t) => t.toLowerCase() !== key) }));
    }

    async function setPrefs(patch) {
        state.prefs = { ...state.prefs, ...patch };
        await db.setMeta('prefs', { data: state.prefs, dirty: true });
        emit('prefs', { source: 'local', keys: Object.keys(patch) });
        broadcast({ type: 'changed' });
        syncer.request(3000);
    }

    function setView(view) {
        state.view = { type: view.type || 'all', id: view.id || null };
        emit('view', state.view);
    }

    function setSearch(q) {
        state.search = q || '';
        emit('view', state.view);
    }

    function select(id) {
        state.selectedId = id || null;
        emit('select', state.selectedId);
    }

    // ------------------------------------------------------------------ Attachments (images)
    const localUrls = new Map();
    const pendingUploads = new Set();
    const attachments = {
        MAX_BYTES: 15 * 1024 * 1024,
        /** Saves images locally (they upload on the next sync) and returns their Markdown. */
        async add(files) {
            const out = [];
            for (const file of files) {
                if (!file.type || !file.type.startsWith('image/') || file.type === 'image/svg+xml') {
                    SN.ui.toast(`"${file.name || 'File'}" isn't a supported image.`, { type: 'warning' });
                    continue;
                }
                if (file.size > attachments.MAX_BYTES) {
                    SN.ui.toast(`"${file.name || 'Image'}" is larger than 15 MB.`, { type: 'warning' });
                    continue;
                }
                const id = util.uuid();
                const name = (file.name && file.name !== 'image.png' ? file.name : `pasted-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.${(file.type.split('/')[1] || 'png').replace('jpeg', 'jpg')}`);
                await db.put('attachments', { id, blob: file, type: file.type, name, size: file.size, createdAt: Date.now(), uploaded: false });
                localUrls.set(id, URL.createObjectURL(file));
                pendingUploads.add(id);
                const alt = name.replace(/\.[^.]+$/, '').replace(/[[\]]/g, '');
                out.push({ id, name, markdown: `![${alt}](/attachments/${id})` });
            }
            if (out.length) syncer.request(500);
            return out;
        },
        localUrl: (id) => localUrls.get(id) || null,
        pendingCount: () => pendingUploads.size,
        /** Loads images that haven't been uploaded yet (on start-up and after every sync). */
        async loadPending() {
            pendingUploads.clear();
            for (const a of await db.getAll('attachments')) {
                if (a.blob && !localUrls.has(a.id)) localUrls.set(a.id, URL.createObjectURL(a.blob));
                if (!a.uploaded && !a.failed) pendingUploads.add(a.id);
            }
        },
    };

    // ------------------------------------------------------------------ Import (.md / .txt)
    async function importFiles(files) {
        const binderByName = new Map(listBinders().map((b) => [b.name.toLowerCase(), b.id]));
        const records = [];
        let order = minSortOrder(state.notes);
        for (const file of files) {
            if (!/\.(md|markdown|txt)$/i.test(file.name) && !/^text\//.test(file.type)) continue;
            if (file.size > 2_000_000) { SN.ui.toast(`"${file.name}" is too large to import.`, { type: 'warning' }); continue; }
            const text = await file.text();
            const { meta, body } = util.parseFrontMatter(text);
            let binderId = state.view.type === 'binder' ? state.view.id : null;
            if (meta.binder) {
                const key = String(meta.binder).toLowerCase();
                if (!binderByName.has(key)) {
                    const b = await createBinder({ name: String(meta.binder).slice(0, 60) });
                    binderByName.set(key, b.id);
                }
                binderId = binderByName.get(key);
            }
            const parseDate = (v) => { const t = Date.parse(v); return Number.isFinite(t) ? t : undefined; };
            records.push(newNoteRecord({
                title: String(meta.title || file.name.replace(/\.(md|markdown|txt)$/i, '')).slice(0, 300),
                content: body,
                binderId,
                tags: Array.isArray(meta.tags) ? meta.tags.map(String).map(util.normalizeTag).filter(Boolean) : (typeof meta.tags === 'string' ? meta.tags.split(',').map(util.normalizeTag).filter(Boolean) : []),
                pinned: meta.pinned === true,
                archived: meta.archived === true,
                color: SN.ui.COLORS.some((c) => c.id && c.id === meta.color) ? meta.color : null,
                icon: typeof meta.icon === 'string' ? meta.icon.slice(0, 40) : null,
                createdAt: parseDate(meta.created),
                updatedAt: parseDate(meta.updated) || file.lastModified || undefined,
                sortOrder: --order,
            }));
        }
        if (records.length) await insertRecords('notes', records);
        return records;
    }

    // ------------------------------------------------------------------ Sync scheduler
    const syncer = {
        status: navigator.onLine ? 'idle' : 'offline',
        error: null,
        lastSyncAt: null,
        pending: 0,
        running: false,
        _timer: null,
        _again: false,
        _enabled: false,

        enable() { this._enabled = true; },

        /** Schedules a sync shortly (debounced). */
        request(delay) {
            if (!this._enabled) return;
            clearTimeout(this._timer);
            this._timer = setTimeout(() => this.now(), delay === undefined ? 1500 : delay);
            this.refreshPending();
        },

        /** Counts unsynced changes from memory (this runs on every keystroke-save, so it must not touch IndexedDB). */
        refreshPending() {
            let pending = attachments.pendingCount();
            for (const n of state.notes.values()) if (n.dirty && n.dirty.length) pending++;
            for (const b of state.binders.values()) if (b.dirty && b.dirty.length) pending++;
            this.pending = pending;
            emit('sync', this);
        },

        async now() {
            if (!this._enabled) return;
            clearTimeout(this._timer);
            if (this.running) { this._again = true; return; }
            if (SN.lock && SN.lock.isLocked()) return;
            if (!navigator.onLine) {
                this.status = 'offline';
                await this.refreshPending();
                if (SN.pwa) SN.pwa.requestBackgroundSync();
                return;
            }
            this.running = true;
            this.status = 'syncing';
            emit('sync', this);
            try {
                const outcome = await SN.sync.run();
                this.status = 'synced';
                this.error = null;
                this.lastSyncAt = Date.now();
                await reloadFromDb('remote');
                if (outcome.uploaded || attachments.pendingCount()) await attachments.loadPending();
                if (outcome.notes.size || outcome.binders.size || outcome.prefs || outcome.pushed) broadcast({ type: 'synced' });
                if (outcome.conflicts.length) emit('conflicts', outcome.conflicts);
                if (outcome.errors.length) SN.ui.toast(outcome.errors[0], { type: 'warning' });
            } catch (e) {
                this.error = e;
                this.status = e.code === 'offline' ? 'offline' : e.code === 'unauthorized' ? 'unauthorized' : e.code === 'locked' ? 'locked' : 'error';
                if (e.code === 'locked' && SN.lock) SN.lock.lock({ reason: 'server' });
                if (e.code === 'unauthorized' && SN.app) SN.app.sessionExpired();
                if (e.code === 'offline' && SN.pwa) SN.pwa.requestBackgroundSync();
            } finally {
                this.running = false;
                await this.refreshPending();
                if (this._again) { this._again = false; this.request(300); }
                else if (this.pending > 0 && this.status === 'synced') this.request(2000);
            }
        },
    };

    if (channel) {
        channel.onmessage = (e) => {
            const msg = e.data || {};
            if (msg.type === 'changed' || msg.type === 'synced') reloadFromDb('remote');
            if (msg.type === 'lock' && SN.lock) SN.lock.lock({ reason: 'other-tab', broadcast: false });
            if (msg.type === 'unlock' && SN.lock) SN.lock.unlocked({ broadcast: false });
            if (msg.type === 'activity' && SN.lock) SN.lock.touch(false);
        };
    }

    SN.store = {
        state, DEFAULT_PREFS, on, emit, broadcast,
        load, reloadFromDb,
        getNote, getBinder, listBinders, visibleNotes, counts, allTags, findNoteByTitle, inView,
        minSortOrder: () => minSortOrder(state.notes),
        createNote, updateNote, updateNotes, deleteNotesForever,
        createBinder, updateBinder, updateBinders, deleteBinder,
        renameTag, removeTag, setPrefs, setView, setSearch, select, importFiles,
    };
    SN.attachments = attachments;
    SN.syncer = syncer;
})(jQuery);
