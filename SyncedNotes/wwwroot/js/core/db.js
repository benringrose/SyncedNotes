/* SyncedNotes – IndexedDB storage. Shared by the page and the service worker. */
(function (root) {
    'use strict';
    const SN = (root.SN = root.SN || {});

    const DB_NAME = 'syncednotes';
    const DB_VERSION = 1;
    const STORES = ['notes', 'binders', 'attachments', 'meta'];
    let dbPromise = null;

    function promisify(request) {
        return new Promise((resolve, reject) => {
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    function open() {
        if (dbPromise) return dbPromise;
        dbPromise = new Promise((resolve, reject) => {
            const request = root.indexedDB.open(DB_NAME, DB_VERSION);
            request.onupgradeneeded = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains('notes')) db.createObjectStore('notes', { keyPath: 'id' });
                if (!db.objectStoreNames.contains('binders')) db.createObjectStore('binders', { keyPath: 'id' });
                if (!db.objectStoreNames.contains('attachments')) db.createObjectStore('attachments', { keyPath: 'id' });
                if (!db.objectStoreNames.contains('meta')) db.createObjectStore('meta', { keyPath: 'key' });
            };
            request.onsuccess = () => {
                const db = request.result;
                // Another tab upgraded the schema: close so the upgrade can proceed.
                db.onversionchange = () => { db.close(); dbPromise = null; };
                resolve(db);
            };
            request.onerror = () => { dbPromise = null; reject(request.error); };
            request.onblocked = () => console.warn('IndexedDB upgrade blocked by another tab.');
        });
        return dbPromise;
    }

    /** Runs fn(stores) inside one transaction and resolves when the transaction commits. */
    async function tx(storeNames, mode, fn) {
        const db = await open();
        const names = Array.isArray(storeNames) ? storeNames : [storeNames];
        return new Promise((resolve, reject) => {
            const t = db.transaction(names, mode);
            const stores = {};
            names.forEach((n) => { stores[n] = t.objectStore(n); });
            let result;
            try {
                result = fn(stores, t);
            } catch (e) {
                try { t.abort(); } catch { /* already finished */ }
                reject(e);
                return;
            }
            t.oncomplete = () => resolve(result);
            t.onerror = () => reject(t.error);
            t.onabort = () => reject(t.error || new Error('Transaction aborted'));
        });
    }

    const db = {
        open,
        tx,
        async get(store, key) {
            const d = await open();
            return promisify(d.transaction(store).objectStore(store).get(key));
        },
        async getAll(store) {
            const d = await open();
            return promisify(d.transaction(store).objectStore(store).getAll());
        },
        put(store, value) {
            return tx(store, 'readwrite', (s) => { s[store].put(value); });
        },
        putMany(store, values) {
            if (!values || !values.length) return Promise.resolve();
            return tx(store, 'readwrite', (s) => { values.forEach((v) => s[store].put(v)); });
        },
        delete(store, key) {
            return tx(store, 'readwrite', (s) => { s[store].delete(key); });
        },
        deleteMany(store, keys) {
            if (!keys || !keys.length) return Promise.resolve();
            return tx(store, 'readwrite', (s) => { keys.forEach((k) => s[store].delete(k)); });
        },
        async getMeta(key) {
            const row = await db.get('meta', key);
            return row ? row.value : undefined;
        },
        setMeta(key, value) {
            return db.put('meta', { key, value });
        },
        deleteMeta(key) {
            return db.delete('meta', key);
        },
        /** Removes every locally stored note, binder, image and setting. */
        wipe() {
            return tx(STORES, 'readwrite', (s) => { STORES.forEach((n) => s[n].clear()); });
        },
    };

    SN.db = db;
})(typeof self !== 'undefined' ? self : this);
