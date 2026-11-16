/* global SN_VERSION, SN_PRECACHE, SN_DEV, SN */
'use strict';

// Shared with the page: IndexedDB access and the sync engine (used for Background Sync).
importScripts('/js/core/util.js', '/js/core/db.js', '/js/core/sync.js');

const CACHE_STATIC = `sn-static-${SN_VERSION}`;
const CACHE_PAGES = 'sn-pages-v1';
const CACHE_ATTACHMENTS = 'sn-attachments-v1';
const SHELL_URL = '/';
const OFFLINE_URL = '/offline';
// Routes rendered by the client-side app (see AddPageRoute in Program.cs).
const APP_PATHS = new Set(['/', '/index', '/new', '/share', '/open-file']);
const NETWORK_TIMEOUT_MS = 4000;

const PLACEHOLDER_IMAGE =
    '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180" viewBox="0 0 320 180">' +
    '<rect width="320" height="180" rx="12" fill="#8882"/>' +
    '<text x="160" y="96" font-family="system-ui,sans-serif" font-size="14" fill="#888" text-anchor="middle">' +
    'Image available when online</text></svg>';

self.addEventListener('install', (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(CACHE_STATIC);
        await Promise.all(SN_PRECACHE.map(async (url) => {
            try {
                // cache: 'reload' bypasses the HTTP cache so a new version never precaches stale files.
                const res = await fetch(new Request(url, { cache: 'reload', credentials: 'same-origin' }));
                if (res.ok) await cache.put(url, res);
            } catch { /* fetched again at runtime */ }
        }));
        // The app shell requires the sign-in cookie, so only cache a real page (never a login redirect).
        try {
            const res = await fetch(SHELL_URL, { credentials: 'same-origin', redirect: 'manual', cache: 'reload' });
            if (res.ok && res.type === 'basic') await (await caches.open(CACHE_PAGES)).put(SHELL_URL, res);
        } catch { /* cached on the next visit */ }
        if (SN_DEV) await self.skipWaiting();
    })());
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keep = new Set([CACHE_STATIC, CACHE_PAGES, CACHE_ATTACHMENTS]);
        for (const key of await caches.keys()) {
            if (key.startsWith('sn-') && !keep.has(key)) await caches.delete(key);
        }
        if (self.registration.navigationPreload) {
            try { await self.registration.navigationPreload.enable(); } catch { /* unsupported */ }
        }
        await self.clients.claim();
    })());
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    if (request.method !== 'GET') return;
    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;
    if (url.pathname.startsWith('/api/') || url.pathname === '/sw.js' || url.pathname === '/healthz') return;

    if (request.mode === 'navigate') {
        event.respondWith(APP_PATHS.has(url.pathname.toLowerCase()) ? appShell(event) : page(event));
        return;
    }
    if (url.pathname.startsWith('/attachments/')) {
        event.respondWith(attachment(request, url));
        return;
    }
    event.respondWith(staticAsset(request));
});

/** Network first (so sign-in redirects keep working), falling back to the cached shell when offline or slow. */
async function appShell(event) {
    const url = new URL(event.request.url);
    const quickAdd = /^\/(new|share)$/i.test(url.pathname);
    if (quickAdd) return quickAddPage(event, url);
    const cachedShell = caches.open(CACHE_PAGES).then((c) => c.match(SHELL_URL, { ignoreVary: true }));
    const network = (async () => {
        const preloaded = await event.preloadResponse;
        const res = preloaded || await fetch(event.request);
        if (res.ok && res.type === 'basic' && !res.redirected) {
            const copy = res.clone();
            caches.open(CACHE_PAGES).then((c) => c.put(SHELL_URL, copy));
        }
        return res;
    })();
    network.catch(() => { /* handled below */ });

    try {
        const winner = await Promise.race([network, new Promise((r) => setTimeout(r, NETWORK_TIMEOUT_MS, null))]);
        if (winner) return winner;
        return (await cachedShell) || await network;
    } catch {
        return (await cachedShell) || (await caches.match(OFFLINE_URL, { ignoreVary: true })) || Response.error();
    }
}

/**
 * Quick-add links are checked by the server (Fetch Metadata), so always ask the network. When offline we can't tell
 * where the link came from, so the cached shell is served with confirm=1 and the app asks before saving.
 */
async function quickAddPage(event, url) {
    try {
        const preloaded = await event.preloadResponse;
        return preloaded || await fetch(event.request);
    } catch {
        if (url.searchParams.get('confirm') !== '1') {
            url.searchParams.set('confirm', '1');
            return Response.redirect(url.href, 302);
        }
        return (await caches.open(CACHE_PAGES).then((c) => c.match(SHELL_URL, { ignoreVary: true }))) || (await caches.match(OFFLINE_URL, { ignoreVary: true })) || Response.error();
    }
}

async function page(event) {
    try {
        const preloaded = await event.preloadResponse;
        return preloaded || await fetch(event.request);
    } catch {
        return (await caches.match(OFFLINE_URL, { ignoreVary: true })) || Response.error();
    }
}

// ASP.NET Core's MapStaticAssets serves fingerprinted URLs such as /js/app/main.y3nuw7yc40.js (immutable).
const FINGERPRINT = /\.[a-z0-9]{10}(\.[a-z0-9]+)$/;

/**
 * Static files are precached per version under their plain paths. Fingerprinted URLs are immutable, so an exact
 * cached copy is always right; otherwise they come from the network (and are cached), and only when offline do we
 * fall back to the precached plain file. Vary is ignored because the server varies on Accept-Encoding but entries
 * were stored under plain URLs.
 */
async function staticAsset(request) {
    const cache = await caches.open(CACHE_STATIC);
    const url = new URL(request.url);
    const fingerprinted = FINGERPRINT.test(url.pathname);
    const cached = await cache.match(request, { ignoreSearch: !fingerprinted, ignoreVary: true });
    if (cached) return cached;
    try {
        const res = await fetch(request);
        if (res.ok && res.type === 'basic' && /\.(css|js|woff2?|png|svg|ico|webmanifest|jpe?g|webp)$/i.test(url.pathname)) {
            cache.put(request, res.clone());
        }
        return res;
    } catch {
        if (fingerprinted) {
            const plain = await cache.match(url.origin + url.pathname.replace(FINGERPRINT, '$1'), { ignoreSearch: true, ignoreVary: true });
            if (plain) return plain;
        }
        return Response.error();
    }
}

/** Images never change once uploaded: cache first, then images still waiting to upload, then the network. */
async function attachment(request, url) {
    const cache = await caches.open(CACHE_ATTACHMENTS);
    const key = url.pathname;
    const cached = await cache.match(key, { ignoreVary: true });
    if (cached) return cached;

    try {
        const id = key.split('/').pop();
        const local = await SN.db.get('attachments', id);
        if (local && local.blob) {
            return new Response(local.blob, { headers: { 'Content-Type': local.type || 'application/octet-stream' } });
        }
    } catch { /* not available locally */ }

    try {
        const res = await fetch(request);
        if (res.ok) await cache.put(key, res.clone());
        return res;
    } catch {
        return new Response(PLACEHOLDER_IMAGE, { headers: { 'Content-Type': 'image/svg+xml' } });
    }
}

// ------------------------------------------------------------------ Background Sync
self.addEventListener('sync', (event) => {
    if (event.tag === 'sn-sync') event.waitUntil(backgroundSync());
});

self.addEventListener('periodicsync', (event) => {
    if (event.tag === 'sn-periodic-sync') event.waitUntil(backgroundSync());
});

async function backgroundSync() {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    if (windows.length) {
        // An open window syncs itself (it also refreshes its UI).
        windows.forEach((w) => w.postMessage({ type: 'sync-requested' }));
        return;
    }
    try {
        await SN.sync.run();
    } catch (e) {
        // Re-throwing tells the browser to retry later; only worth it when we were offline.
        if (e && e.code === 'offline') throw e;
    }
}

// ------------------------------------------------------------------ Messages from the page
self.addEventListener('message', (event) => {
    const data = event.data || {};
    if (data.type === 'SKIP_WAITING') {
        self.skipWaiting();
    } else if (data.type === 'CLEAR_USER_DATA') {
        event.waitUntil(Promise.all([caches.delete(CACHE_PAGES), caches.delete(CACHE_ATTACHMENTS)]));
    } else if (data.type === 'GET_VERSION' && event.source) {
        event.source.postMessage({ type: 'VERSION', version: SN_VERSION });
    }
});
