/* SyncedNotes – PWA: service worker, updates, install prompt, background sync and launch handling. */
(function ($) {
    'use strict';
    const { ui } = SN;
    let deferredPrompt = null;
    let updateRequested = false;

    async function register() {
        if (!('serviceWorker' in navigator)) return;
        try {
            const reg = await navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' });
            if (reg.waiting && navigator.serviceWorker.controller) promptUpdate(reg.waiting);
            reg.addEventListener('updatefound', () => {
                const worker = reg.installing;
                if (!worker) return;
                worker.addEventListener('statechange', () => {
                    if (worker.state === 'installed' && navigator.serviceWorker.controller) promptUpdate(worker);
                });
            });
            let reloading = false;
            navigator.serviceWorker.addEventListener('controllerchange', () => {
                if (!updateRequested || reloading) return;
                reloading = true;
                location.reload();
            });
            // Check for a new version every hour and whenever the app comes back to the foreground.
            setInterval(() => reg.update().catch(() => { }), 60 * 60 * 1000);
            document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') reg.update().catch(() => { }); });
            registerPeriodicSync(reg);
        } catch (e) {
            console.warn('Service worker registration failed:', e);
        }
    }

    function promptUpdate(worker) {
        ui.toast('A new version of SyncedNotes is ready.', {
            action: 'Update', timeout: 0, icon: 'stars',
            onAction: async () => {
                updateRequested = true;
                try { await SN.editor.flush(); } catch { /* ignore */ }
                worker.postMessage({ type: 'SKIP_WAITING' });
            },
        });
    }

    async function registerPeriodicSync(reg) {
        try {
            if (!('periodicSync' in reg) || !navigator.permissions) return;
            const status = await navigator.permissions.query({ name: 'periodic-background-sync' });
            if (status.state === 'granted') await reg.periodicSync.register('sn-periodic-sync', { minInterval: 6 * 60 * 60 * 1000 });
        } catch { /* not supported */ }
    }

    /** Asks the browser to sync in the background once a connection is available (even if the app is closed). */
    async function requestBackgroundSync() {
        try {
            if (!('serviceWorker' in navigator)) return;
            const reg = await navigator.serviceWorker.ready;
            if (reg.sync) await reg.sync.register('sn-sync');
        } catch { /* not supported */ }
    }

    async function clearCaches() {
        try {
            if (navigator.serviceWorker && navigator.serviceWorker.controller) navigator.serviceWorker.controller.postMessage({ type: 'CLEAR_USER_DATA' });
            if (window.caches) await Promise.all(['sn-pages-v1', 'sn-attachments-v1'].map((c) => caches.delete(c)));
        } catch { /* ignore */ }
    }

    function init() {
        window.addEventListener('beforeinstallprompt', (e) => {
            e.preventDefault();
            deferredPrompt = e;
            $('[data-install-only]').prop('hidden', false);
        });
        window.addEventListener('appinstalled', () => {
            deferredPrompt = null;
            $('[data-install-only]').prop('hidden', true);
            ui.toast('SyncedNotes is installed. Find it in your apps.', { type: 'success' });
        });
        if (navigator.serviceWorker) {
            navigator.serviceWorker.addEventListener('message', (e) => {
                if (e.data && e.data.type === 'sync-requested') SN.syncer.now();
            });
        }
        register();
    }

    /** Files opened with the installed app (file handlers) and captured links (launch_handler). */
    function initLaunchQueue(handler) {
        if ('launchQueue' in window && window.launchQueue && typeof window.launchQueue.setConsumer === 'function') {
            window.launchQueue.setConsumer(handler);
        }
    }

    SN.pwa = {
        init, initLaunchQueue, requestBackgroundSync, clearCaches,
        canInstall: () => !!deferredPrompt,
        async install() {
            if (!deferredPrompt) return;
            deferredPrompt.prompt();
            try { await deferredPrompt.userChoice; } catch { /* dismissed */ }
            deferredPrompt = null;
            $('[data-install-only]').prop('hidden', true);
        },
    };
})(jQuery);
