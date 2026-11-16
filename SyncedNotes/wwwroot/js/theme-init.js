/* Applies the saved theme before first paint (loaded synchronously in <head>). */
(function () {
    'use strict';
    var KEY = 'sn.theme';
    var COLORS = { light: '#f3f4f7', dark: '#111318' };
    var ORDER = ['auto', 'light', 'dark'];

    function preference() {
        try { return localStorage.getItem(KEY) || 'auto'; } catch (e) { return 'auto'; }
    }

    function systemDark() {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    }

    function apply(pref) {
        if (ORDER.indexOf(pref) < 0) pref = 'auto';
        var mode = pref === 'dark' || (pref === 'auto' && systemDark()) ? 'dark' : 'light';
        var root = document.documentElement;
        root.setAttribute('data-bs-theme', mode);
        root.setAttribute('data-theme-pref', pref);
        var metas = document.querySelectorAll('meta[name="theme-color"]');
        for (var i = 0; i < metas.length; i++) {
            var isDarkMeta = (metas[i].getAttribute('media') || '').indexOf('dark') >= 0;
            metas[i].setAttribute('content', pref === 'auto' ? (isDarkMeta ? COLORS.dark : COLORS.light) : COLORS[mode]);
        }
        return mode;
    }

    window.SNTheme = {
        get: preference,
        mode: function () { return document.documentElement.getAttribute('data-bs-theme'); },
        set: function (pref) {
            try { localStorage.setItem(KEY, pref); } catch (e) { /* private mode */ }
            var mode = apply(pref);
            document.dispatchEvent(new CustomEvent('sn:theme', { detail: { pref: pref, mode: mode } }));
            return mode;
        },
        cycle: function () {
            var next = ORDER[(ORDER.indexOf(preference()) + 1) % ORDER.length];
            window.SNTheme.set(next);
            return next;
        },
        label: function (pref) {
            return { auto: 'System', light: 'Light', dark: 'Dark' }[pref || preference()];
        },
    };

    apply(preference());

    if (window.matchMedia) {
        var mq = window.matchMedia('(prefers-color-scheme: dark)');
        var onChange = function () {
            if (preference() === 'auto') {
                var mode = apply('auto');
                document.dispatchEvent(new CustomEvent('sn:theme', { detail: { pref: 'auto', mode: mode } }));
            }
        };
        if (mq.addEventListener) mq.addEventListener('change', onChange); else if (mq.addListener) mq.addListener(onChange);
    }

    // Other tabs changed the theme.
    window.addEventListener('storage', function (e) {
        if (e.key === KEY) apply(preference());
    });
})();
