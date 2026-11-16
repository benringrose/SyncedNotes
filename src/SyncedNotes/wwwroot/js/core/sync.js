/* SyncedNotes – sync engine. Shared by the page and the service worker (Background Sync).
 *
 * Local records carry:
 *   rev    – the last server revision this device saw (0 = never synced)
 *   dirty  – names of fields changed locally since `rev` ('*' = everything, i.e. a new record)
 *   lv     – a local version counter bumped on every local write
 * A push sends dirty records with their base revision; the server merges field by field and reports
 * conflicts (both sides edited the text), saving this device's text as a separate "conflicted copy".
 */
(function (root) {
    'use strict';
    const SN = (root.SN = root.SN || {});

    const MAX_NOTES_PER_PUSH = 100;
    const MAX_PUSH_BYTES = 4 * 1024 * 1024;
    const ATTACHMENT_CACHE = 'sn-attachments-v1';

    class SyncError extends Error {
        constructor(code, message, status) {
            super(message || code);
            this.code = code;
            this.status = status || 0;
        }
    }

    async function request(method, url, body, extraHeaders) {
        const headers = Object.assign({ 'X-SyncedNotes': '1', Accept: 'application/json' }, extraHeaders || {});
        let payload = body;
        if (body !== undefined && body !== null && !(body instanceof Blob)) {
            headers['Content-Type'] = 'application/json';
            payload = JSON.stringify(body);
        }
        let res;
        try {
            res = await fetch(url, { method, headers, body: payload, credentials: 'same-origin', cache: 'no-store' });
        } catch {
            throw new SyncError('offline', 'You appear to be offline.');
        }
        if (res.status === 401) throw new SyncError('unauthorized', 'Your session has expired. Sign in again to sync.', 401);
        if (res.status === 423) throw new SyncError('locked', 'The app is locked.', 423);
        if (!res.ok) {
            let detail = '';
            try { const p = await res.json(); detail = p.detail || p.title || ''; } catch { /* not JSON */ }
            throw new SyncError('server', detail || `The server returned an error (${res.status}).`, res.status);
        }
        if (res.status === 204) return null;
        const type = res.headers.get('content-type') || '';
        return type.includes('json') ? res.json() : res.text();
    }

    /** Ensures only one sync runs at a time across every tab and the service worker. */
    let localChain = Promise.resolve();
    function withLock(fn) {
        const locks = root.navigator && root.navigator.locks;
        if (locks && typeof locks.request === 'function') return locks.request('syncednotes-sync', fn);
        const run = localChain.then(fn, fn);
        localChain = run.catch(() => { });
        return run;
    }

    const isDirty = (r) => !!(r && r.dirty && r.dirty.length);

    function toNoteChange(n) {
        const all = !n.rev || n.dirty.includes('*');
        return {
            id: n.id,
            baseRev: n.rev || 0,
            fields: all ? null : n.dirty.filter((f) => f !== 'deleted' && f !== '*'),
            binderId: n.binderId || null,
            title: n.title || '',
            content: n.content || '',
            color: n.color || null,
            icon: n.icon || null,
            tags: n.tags || [],
            pinned: !!n.pinned,
            archived: !!n.archived,
            trashedAt: n.trashedAt || null,
            sortOrder: n.sortOrder || 0,
            createdAt: n.createdAt || 0,
            updatedAt: n.updatedAt || 0,
            deleted: !!n.deleted,
        };
    }

    function toBinderChange(b) {
        const all = !b.rev || b.dirty.includes('*');
        return {
            id: b.id,
            baseRev: b.rev || 0,
            fields: all ? null : b.dirty.filter((f) => f !== 'deleted' && f !== '*'),
            name: b.name || '',
            color: b.color || null,
            icon: b.icon || null,
            sortOrder: b.sortOrder || 0,
            createdAt: b.createdAt || 0,
            updatedAt: b.updatedAt || 0,
            deleted: !!b.deleted,
        };
    }

    function fromServerNote(n, local) {
        return {
            id: n.id,
            binderId: n.binderId || null,
            title: n.title || '',
            content: n.content || '',
            color: n.color || null,
            icon: n.icon || null,
            tags: n.tags || [],
            pinned: !!n.pinned,
            archived: !!n.archived,
            trashedAt: n.trashedAt || null,
            sortOrder: n.sortOrder || 0,
            createdAt: n.createdAt,
            updatedAt: n.updatedAt,
            rev: n.rev,
            deleted: false,
            dirty: [],
            lv: local ? local.lv || 0 : 0,
        };
    }

    function fromServerBinder(b, local) {
        return {
            id: b.id,
            name: b.name,
            color: b.color || null,
            icon: b.icon || null,
            sortOrder: b.sortOrder || 0,
            createdAt: b.createdAt,
            updatedAt: b.updatedAt,
            rev: b.rev,
            deleted: false,
            dirty: [],
            lv: local ? local.lv || 0 : 0,
        };
    }

    /** Uploads images that were pasted/dropped while offline, then moves them into the attachment cache. */
    async function uploadAttachments(outcome) {
        const pending = (await SN.db.getAll('attachments')).filter((a) => !a.uploaded && !a.failed);
        for (const a of pending) {
            try {
                await request('PUT', `/api/attachments/${a.id}`, a.blob, {
                    'Content-Type': a.type || 'application/octet-stream',
                    'X-File-Name': encodeURIComponent(a.name || 'image'),
                });
            } catch (e) {
                if (e.code === 'server' && [400, 409, 413, 415].includes(e.status)) {
                    a.failed = e.message;
                    await SN.db.put('attachments', a);
                    outcome.errors.push(`Image "${a.name}" couldn't be uploaded: ${e.message}`);
                    continue;
                }
                throw e;
            }
            outcome.uploaded++;
            // Keep a copy for offline use in the cache the service worker serves images from.
            if (root.caches) {
                try {
                    const cache = await root.caches.open(ATTACHMENT_CACHE);
                    await cache.put(`/attachments/${a.id}`, new Response(a.blob, {
                        headers: { 'Content-Type': a.type || 'application/octet-stream', 'Cache-Control': 'private, max-age=31536000, immutable' },
                    }));
                    await SN.db.delete('attachments', a.id);
                    continue;
                } catch { /* fall back to keeping it in IndexedDB */ }
            }
            a.uploaded = true;
            await SN.db.put('attachments', a);
        }
    }

    /** Collects the next batch of dirty records, skipping ones already pushed during this run. */
    async function collectPushBatch(pushed, prefsPushed) {
        const [binders, notes, prefs] = await Promise.all([
            SN.db.getAll('binders'), SN.db.getAll('notes'), SN.db.getMeta('prefs'),
        ]);
        const dirtyBinders = binders.filter((b) => isDirty(b) && !pushed.has('b:' + b.id));
        const dirtyNotes = notes.filter((n) => isDirty(n) && !pushed.has('n:' + n.id));
        const batch = [];
        let bytes = 0;
        for (const n of dirtyNotes) {
            const size = ((n.content || '').length + (n.title || '').length) * 2 + 512;
            if (batch.length && (batch.length >= MAX_NOTES_PER_PUSH || bytes + size > MAX_PUSH_BYTES)) break;
            batch.push(n);
            bytes += size;
        }
        return {
            binders: dirtyBinders,
            notes: batch,
            prefs: prefs && prefs.dirty && !prefsPushed ? prefs : null,
            remaining: dirtyNotes.length - batch.length,
        };
    }

    async function applyResults(results, snapshot, outcome) {
        if (!results) return;
        await SN.db.tx(['notes', 'binders'], 'readwrite', (s) => {
            for (const r of results.binders || []) {
                const req = s.binders.get(r.id);
                req.onsuccess = () => {
                    const local = req.result;
                    if (!local) return;
                    const unchanged = snapshot.get('b:' + r.id) === local.lv;
                    if (r.status === 'rejected') {
                        outcome.errors.push(`Binder "${local.name}" wasn't synced: ${r.error || 'rejected'}`);
                        if (unchanged) local.dirty = [];
                        s.binders.put(local);
                        return;
                    }
                    if (r.rev) local.rev = r.rev;
                    if (unchanged) local.dirty = [];
                    if (local.deleted && !local.dirty.length) s.binders.delete(r.id);
                    else s.binders.put(local);
                };
            }
            for (const r of results.notes || []) {
                const req = s.notes.get(r.id);
                req.onsuccess = () => {
                    const local = req.result;
                    if (!local) return;
                    const unchanged = snapshot.get('n:' + r.id) === local.lv;
                    if (r.status === 'rejected') {
                        outcome.errors.push(`"${local.title || 'Untitled'}" wasn't synced: ${r.error || 'rejected'}`);
                        if (unchanged) local.dirty = [];
                        s.notes.put(local);
                        return;
                    }
                    if (r.status === 'conflict') {
                        outcome.conflicts.push({ id: r.id, copyId: r.copyId || null, title: local.title });
                        // Text typed while the request was in flight belongs in the conflicted copy.
                        if (r.copyId && !unchanged) outcome.pendingCopies.set(r.copyId, local.content);
                        // The server's version arrives with the pull and replaces this record.
                        local.dirty = [];
                        local.deleted = false;
                        if (r.rev) local.rev = r.rev;
                        s.notes.put(local);
                        return;
                    }
                    if (r.rev) local.rev = r.rev;
                    if (unchanged) local.dirty = [];
                    if (local.deleted && !local.dirty.length) s.notes.delete(r.id);
                    else s.notes.put(local);
                };
            }
        });
    }

    async function applyPull(res, outcome) {
        if (res.reset) {
            // The server's history doesn't match ours (e.g. it was restored from a backup): re-download
            // everything, keeping unsynced local work so it is pushed again.
            await SN.db.tx(['notes', 'binders'], 'readwrite', (s) => {
                for (const name of ['notes', 'binders']) {
                    const req = s[name].getAll();
                    req.onsuccess = () => {
                        for (const r of req.result) {
                            if (isDirty(r)) { r.rev = 0; r.dirty = ['*']; s[name].put(r); }
                            else s[name].delete(r.id);
                        }
                    };
                }
            });
            outcome.reset = true;
        }

        await SN.db.tx(['notes', 'binders', 'meta'], 'readwrite', (s) => {
            for (const b of res.binders || []) {
                const req = s.binders.get(b.id);
                req.onsuccess = () => {
                    const local = req.result;
                    if (isDirty(local)) return; // local edits win until they have been pushed
                    if (b.deleted) { if (local) s.binders.delete(b.id); }
                    else s.binders.put(fromServerBinder(b, local));
                    outcome.binders.add(b.id);
                };
            }
            for (const n of res.notes || []) {
                const req = s.notes.get(n.id);
                req.onsuccess = () => {
                    const local = req.result;
                    if (isDirty(local)) return;
                    if (n.deleted) { if (local) s.notes.delete(n.id); }
                    else s.notes.put(fromServerNote(n, local));
                    outcome.notes.add(n.id);
                };
            }
            if (res.preferences) {
                const req = s.meta.get('prefs');
                req.onsuccess = () => {
                    const current = req.result && req.result.value;
                    if (current && current.dirty) return;
                    s.meta.put({ key: 'prefs', value: { data: res.preferences.data, rev: res.preferences.rev, dirty: false } });
                    outcome.prefs = true;
                };
            }
            s.meta.put({ key: 'cursor', value: res.cursor });
        });
    }

    /** Puts text typed during a conflicting push into the conflicted copy so it is not lost. */
    async function applyPendingCopies(outcome) {
        if (!outcome.pendingCopies.size) return;
        await SN.db.tx('notes', 'readwrite', (s) => {
            for (const [copyId, content] of outcome.pendingCopies) {
                const req = s.notes.get(copyId);
                req.onsuccess = () => {
                    const copy = req.result;
                    if (!copy) return;
                    copy.content = content;
                    copy.updatedAt = Date.now();
                    copy.dirty = Array.from(new Set([...(copy.dirty || []), 'content']));
                    copy.lv = (copy.lv || 0) + 1;
                    s.notes.put(copy);
                    outcome.notes.add(copyId);
                };
            }
        });
        outcome.pendingCopies.clear();
    }

    async function runCore() {
        const outcome = {
            ok: false, notes: new Set(), binders: new Set(), prefs: false, reset: false,
            conflicts: [], pendingCopies: new Map(), errors: [], uploaded: 0, pushed: 0,
        };
        const userId = await SN.db.getMeta('userId');
        if (!userId) throw new SyncError('no-user', 'Not signed in on this device.');

        await uploadAttachments(outcome);

        let cursor = (await SN.db.getMeta('cursor')) || 0;
        let hasMore = true;
        // Each record is pushed at most once per run; edits made meanwhile go out with the next sync.
        const pushed = new Set();
        let prefsPushed = false;
        for (let round = 0; round < 1000; round++) {
            const batch = await collectPushBatch(pushed, prefsPushed);
            if (!batch.binders.length && !batch.notes.length && !batch.prefs) break;

            const snapshot = new Map();
            batch.binders.forEach((b) => { snapshot.set('b:' + b.id, b.lv); pushed.add('b:' + b.id); });
            batch.notes.forEach((n) => { snapshot.set('n:' + n.id, n.lv); pushed.add('n:' + n.id); });
            if (batch.prefs) prefsPushed = true;

            const res = await request('POST', '/api/sync', {
                cursor,
                binders: batch.binders.map(toBinderChange),
                notes: batch.notes.map(toNoteChange),
                preferences: batch.prefs ? batch.prefs.data : undefined,
            });
            outcome.pushed += batch.binders.length + batch.notes.length;
            if (batch.prefs) {
                const current = await SN.db.getMeta('prefs');
                // Only mark clean if the preferences didn't change again during the request.
                if (current && JSON.stringify(current.data) === JSON.stringify(batch.prefs.data)) {
                    current.dirty = false;
                    await SN.db.setMeta('prefs', current);
                }
            }
            await applyResults(res.results, snapshot, outcome);
            await applyPull(res, outcome);
            await applyPendingCopies(outcome);
            cursor = res.cursor;
            hasMore = res.hasMore;
        }

        for (let page = 0; page < 1000; page++) {
            const res = await request('GET', `/api/sync?since=${encodeURIComponent(cursor)}`);
            await applyPull(res, outcome);
            cursor = res.cursor;
            hasMore = res.hasMore;
            if (!hasMore) break;
        }

        await SN.db.setMeta('lastSyncAt', Date.now());
        outcome.ok = true;
        return outcome;
    }

    SN.sync = {
        SyncError,
        ATTACHMENT_CACHE,
        request,
        /** Runs a full sync (upload images, push local changes, pull remote changes). */
        run: () => withLock(runCore),
        /** Number of local changes waiting to be pushed. */
        async pendingCount() {
            const [notes, binders, atts] = await Promise.all([SN.db.getAll('notes'), SN.db.getAll('binders'), SN.db.getAll('attachments')]);
            return notes.filter(isDirty).length + binders.filter(isDirty).length + atts.filter((a) => !a.uploaded && !a.failed).length;
        },
    };
})(typeof self !== 'undefined' ? self : this);
