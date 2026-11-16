/* SyncedNotes – UI helpers: toasts, dialogs, context menus, pickers and the JSON API helper. */
(function ($) {
    'use strict';
    const { util } = SN;
    const esc = util.escapeHtml;
    const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

    const COLORS = [
        { id: null, name: 'None' },
        { id: 'red', name: 'Red' }, { id: 'orange', name: 'Orange' }, { id: 'yellow', name: 'Yellow' },
        { id: 'green', name: 'Green' }, { id: 'teal', name: 'Teal' }, { id: 'blue', name: 'Blue' },
        { id: 'indigo', name: 'Indigo' }, { id: 'purple', name: 'Purple' }, { id: 'pink', name: 'Pink' },
        { id: 'brown', name: 'Brown' }, { id: 'gray', name: 'Grey' },
    ];

    const ICONS = [
        'journal-text', 'file-earmark-text', 'sticky', 'star', 'heart', 'bookmark', 'lightbulb', 'check2-square',
        'list-check', 'calendar-event', 'clock', 'alarm', 'flag', 'pin-angle', 'briefcase', 'house-door',
        'building', 'graph-up-arrow', 'cash-coin', 'cart', 'bag', 'gift', 'trophy', 'bicycle',
        'heart-pulse', 'activity', 'cup-hot', 'egg-fried', 'airplane', 'car-front', 'geo-alt', 'globe2',
        'book', 'mortarboard', 'code-slash', 'terminal', 'bug', 'cpu', 'database', 'cloud',
        'music-note-beamed', 'camera', 'film', 'palette', 'brush', 'tools', 'gear', 'shield-lock',
        'key', 'people', 'person', 'chat-dots', 'envelope', 'telephone', 'emoji-smile', 'tree',
        'flower1', 'sun', 'moon-stars', 'fire', 'lightning-charge', 'rocket-takeoff', 'stars', 'puzzle',
    ].map((n) => 'bi-' + n);

    const EMOJI = ('📝 📌 ⭐ ❤️ 💡 ✅ 📅 ⏰ 🚩 💼 🏠 🏢 📈 💰 🛒 🎁 🏆 🚴 ⚽ 🏀 🎾 🏃 🏋️ 🧘 🍳 ☕ ✈️ 🚗 🌍 📚 🎓 💻 🐛 ⚙️ 🔒 🔑 👥 💬 📧 📞 😀 🌳 🌸 ☀️ 🌙 🔥 ⚡ 🚀 ✨ 🧩 🎵 📷 🎬 🎨 🛠️ 🧪 🩺 🐶 🐱').split(' ');

    // ------------------------------------------------------------------ Toasts
    function toast(message, opts) {
        opts = opts || {};
        const type = opts.type || 'info';
        const icon = opts.icon || { success: 'check-circle-fill', danger: 'exclamation-octagon-fill', warning: 'exclamation-triangle-fill', info: 'info-circle-fill' }[type];
        const $t = $(`<div class="sn-toast ${type}" role="status">
            <i class="bi bi-${icon}"></i><div class="msg"></div>
            ${opts.action ? `<button type="button" class="toast-action">${esc(opts.action)}</button>` : ''}
            <button type="button" class="toast-close" aria-label="Dismiss"><i class="bi bi-x-lg"></i></button></div>`);
        $t.find('.msg').text(message);
        const $stack = $('#toasts');
        $stack.children().slice(0, -1).each(function () { $(this).remove(); }); // keep at most 2
        $stack.append($t);
        let timer = null;
        const close = () => {
            clearTimeout(timer);
            $t.addClass('leaving');
            setTimeout(() => $t.remove(), 200);
        };
        $t.find('.toast-close').on('click', close);
        $t.find('.toast-action').on('click', () => { close(); if (opts.onAction) opts.onAction(); });
        const timeout = opts.timeout === undefined ? (opts.action ? 7000 : 4000) : opts.timeout;
        if (timeout > 0) {
            timer = setTimeout(close, timeout);
            $t.on('mouseenter', () => clearTimeout(timer)).on('mouseleave', () => { timer = setTimeout(close, 2500); });
        }
        return { close };
    }

    // ------------------------------------------------------------------ Confirm / prompt
    function modal(id) {
        return bootstrap.Modal.getOrCreateInstance(document.getElementById(id));
    }

    function confirm(opts) {
        return new Promise((resolve) => {
            const $m = $('#confirmModal');
            $('#confirmTitle').text(opts.title || 'Are you sure?');
            if (opts.bodyHtml) $('#confirmBody').html(opts.bodyHtml); else $('#confirmBody').text(opts.body || '');
            const $ok = $('#confirmOk').text(opts.okText || 'OK')
                .removeClass('btn-primary btn-danger').addClass(opts.danger ? 'btn-danger' : 'btn-primary');
            let result = false;
            $ok.off('click').on('click', () => { result = true; modal('confirmModal').hide(); });
            $m.one('shown.bs.modal', () => $ok.trigger('focus'));
            $m.one('hidden.bs.modal', () => resolve(result));
            modal('confirmModal').show();
        });
    }

    function prompt(opts) {
        return new Promise((resolve) => {
            const $m = $('#promptModal');
            $('#promptTitle').text(opts.title || '');
            $('#promptLabel').text(opts.label || '');
            $('#promptHelp').text(opts.help || '');
            const $input = $('#promptInput').val(opts.value || '').attr({
                placeholder: opts.placeholder || '', type: opts.type || 'text', inputmode: opts.inputmode || null,
                maxlength: opts.maxlength || 300, pattern: opts.pattern || null,
            });
            $('#promptOk').text(opts.okText || 'OK');
            let result = null;
            $('#promptForm').off('submit').on('submit', (e) => {
                e.preventDefault();
                const v = String($input.val() || '').trim();
                if (opts.validate) {
                    const err = opts.validate(v);
                    if (err) { $('#promptHelp').text(err).addClass('text-danger'); return; }
                }
                result = v;
                modal('promptModal').hide();
            });
            $m.one('shown.bs.modal', () => { $('#promptHelp').removeClass('text-danger'); $input.trigger('focus').trigger('select'); });
            $m.one('hidden.bs.modal', () => resolve(result));
            modal('promptModal').show();
        });
    }

    // ------------------------------------------------------------------ Context menu
    let $ctx = null;
    function closeContextMenu() {
        if ($ctx) { $ctx.remove(); $ctx = null; }
        $(document).off('.snctx');
    }

    function buildMenu(items) {
        const $ul = $('<ul class="dropdown-menu show" role="menu"></ul>');
        items.filter(Boolean).forEach((item) => {
            if (item === '-') { $ul.append('<li><hr class="dropdown-divider"></li>'); return; }
            if (item.header) { $ul.append($('<li><h6 class="dropdown-header"></h6></li>').find('h6').text(item.header).end()); return; }
            const $li = $('<li></li>');
            const $btn = $(`<button type="button" class="dropdown-item${item.danger ? ' danger' : ''}${item.checked ? ' checked' : ''}" role="menuitem"></button>`);
            $btn.append(item.iconHtml || (item.icon ? `<i class="bi ${item.icon}"></i>` : '<i class="bi"></i>'));
            $btn.append($('<span></span>').text(item.label));
            if (item.shortcut) $btn.append($('<span class="shortcut"></span>').text(formatShortcut(item.shortcut)));
            if (item.disabled) $btn.prop('disabled', true);
            if (item.submenu) {
                $li.addClass('ctx-submenu');
                $li.append($btn, buildMenu(item.submenu).removeClass('show'));
                $btn.on('click', (e) => { e.stopPropagation(); $li.toggleClass('open'); });
            } else {
                $btn.on('click', (e) => {
                    e.stopPropagation();
                    closeContextMenu();
                    if (item.action) item.action();
                });
                $li.append($btn);
            }
            $ul.append($li);
        });
        return $ul;
    }

    function contextMenu(pos, items) {
        closeContextMenu();
        $ctx = $('<div class="ctx-menu"></div>').append(buildMenu(items)).appendTo(document.body);
        const menu = $ctx[0];
        const w = menu.offsetWidth, h = menu.offsetHeight;
        const x = Math.min(pos.x, window.innerWidth - w - 8);
        const y = pos.y + h > window.innerHeight - 8 ? Math.max(8, pos.y - h) : pos.y;
        $ctx.css({ left: Math.max(8, x), top: y });
        // Open submenus to the left when there is no room on the right.
        if (x + w * 2 > window.innerWidth) $ctx.find('.ctx-submenu > .dropdown-menu').css({ left: 'auto', right: '100%' });
        $ctx.find('.dropdown-item:not(:disabled)').first().trigger('focus');
        setTimeout(() => {
            $(document).on('mousedown.snctx touchstart.snctx', (e) => { if (!$(e.target).closest('.ctx-menu').length) closeContextMenu(); });
            $(document).on('keydown.snctx', (e) => {
                const $items = $ctx.find('.dropdown-item:visible:not(:disabled)');
                const i = $items.index(document.activeElement);
                if (e.key === 'Escape') { e.preventDefault(); closeContextMenu(); }
                else if (e.key === 'ArrowDown') { e.preventDefault(); $items.eq((i + 1) % $items.length).trigger('focus'); }
                else if (e.key === 'ArrowUp') { e.preventDefault(); $items.eq((i - 1 + $items.length) % $items.length).trigger('focus'); }
            });
            $(window).one('blur.snctx resize.snctx', closeContextMenu);
            $('.note-list-scroll, .sidebar-scroll').one('scroll.snctx', closeContextMenu);
        }, 0);
    }

    // ------------------------------------------------------------------ Colour & icon picker
    function iconHtml(icon, fallback) {
        const value = icon || fallback;
        if (!value) return '';
        if (value.startsWith('bi-')) return `<i class="bi ${esc(value)}" aria-hidden="true"></i>`;
        return `<span class="emoji" aria-hidden="true">${esc(value)}</span>`;
    }

    const colorClass = (color) => (color ? `c-${color}` : '');

    function pickStyle(opts) {
        return new Promise((resolve) => {
            let color = opts.color || null;
            let icon = opts.icon || null;
            let tab = icon && !icon.startsWith('bi-') ? 'emoji' : 'icons';
            const $m = $('#styleModal');
            $('#styleTitle').text(opts.title || 'Colour & icon');
            $('#styleNameRow').prop('hidden', !opts.withName);
            $('#styleName').val(opts.name || '').prop('required', !!opts.withName);
            $('#styleSave').text(opts.okText || 'Save');

            const renderSwatches = () => {
                $('#styleSwatches').html(COLORS.map((c) =>
                    `<button type="button" class="swatch ${colorClass(c.id)}${(c.id || null) === color ? ' selected' : ''}" data-color="${c.id || ''}" title="${c.name}" aria-label="${c.name}">
                        ${c.id ? '' : '<i class="bi bi-slash-circle"></i>'}${(c.id || null) === color ? '<i class="bi bi-check-lg"></i>' : ''}</button>`).join(''));
            };
            const renderIcons = () => {
                const filter = String($('#styleIconSearch').val() || '').toLowerCase().trim();
                const list = tab === 'icons' ? ICONS.filter((i) => !filter || i.includes(filter)) : EMOJI;
                $('#styleIconSearch').prop('hidden', tab !== 'icons');
                $('#styleIconTabs [data-icon-tab]').each(function () { $(this).toggleClass('active', $(this).data('iconTab') === tab); });
                $('#styleIconGrid').html(list.map((i) =>
                    `<button type="button" class="icon-option${i === icon ? ' selected' : ''}" data-icon="${esc(i)}" title="${esc(i.replace(/^bi-/, ''))}">${iconHtml(i)}</button>`).join(''));
            };
            renderSwatches();
            renderIcons();

            $('#styleSwatches').off('click').on('click', '.swatch', function () { color = $(this).data('color') || null; renderSwatches(); });
            $('#styleIconGrid').off('click').on('click', '.icon-option', function () { icon = String($(this).data('icon')); renderIcons(); });
            $('#styleIconTabs').off('click').on('click', '[data-icon-tab]', function () { tab = $(this).data('iconTab'); renderIcons(); });
            $('#styleIconSearch').off('input').on('input', renderIcons).val('');
            $('#styleClearIcon').off('click').on('click', () => { icon = null; renderIcons(); });

            let result = null;
            $('#styleForm').off('submit').on('submit', (e) => {
                e.preventDefault();
                const name = String($('#styleName').val() || '').trim();
                if (opts.withName && !name) { $('#styleName').trigger('focus'); return; }
                result = { color, icon, name };
                modal('styleModal').hide();
            });
            $m.one('shown.bs.modal', () => { if (opts.withName) $('#styleName').trigger('focus').trigger('select'); });
            $m.one('hidden.bs.modal', () => resolve(result));
            modal('styleModal').show();
        });
    }

    // ------------------------------------------------------------------ Misc helpers
    function isTyping(el) {
        el = el || document.activeElement;
        if (!el) return false;
        const tag = el.tagName;
        return tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable ||
            (tag === 'INPUT' && !['checkbox', 'radio', 'button', 'submit', 'range'].includes(el.type));
    }

    async function copyText(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            const ta = Object.assign(document.createElement('textarea'), { value: text });
            ta.style.cssText = 'position:fixed;opacity:0';
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand('copy');
            ta.remove();
            return ok;
        }
    }

    function download(filename, data, type) {
        const blob = data instanceof Blob ? data : new Blob([data], { type: type || 'text/plain;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const a = Object.assign(document.createElement('a'), { href: url, download: filename });
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 2000);
    }

    function formatShortcut(text) {
        if (!text) return '';
        return isMac ? text.replace(/Ctrl/g, '⌘').replace(/Alt/g, '⌥').replace(/Shift/g, '⇧') : text;
    }

    /** JSON API helper for account/settings endpoints (sync uses SN.sync.request). */
    async function api(method, url, body) {
        const headers = { 'X-SyncedNotes': '1', Accept: 'application/json' };
        const init = { method, headers, credentials: 'same-origin', cache: 'no-store' };
        if (body !== undefined) { headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
        let res;
        try {
            res = await fetch(url, init);
        } catch {
            const e = new Error('You\'re offline. Connect to the internet and try again.');
            e.offline = true;
            throw e;
        }
        let data = null;
        if (res.status !== 204) { try { data = await res.json(); } catch { /* empty */ } }
        if (!res.ok) {
            if (res.status === 423 && SN.lock) SN.lock.lock({ reason: 'server' });
            if (res.status === 401 && SN.app) SN.app.sessionExpired();
            const e = new Error((data && (data.detail ? `${data.title} ${data.detail}` : data.title)) || `Request failed (${res.status}).`);
            e.status = res.status;
            throw e;
        }
        return data;
    }

    // Esc closes the topmost dialog even if focus hasn't moved into it yet (Bootstrap only listens on the dialog)
    // or it's still animating open (Bootstrap ignores hide() during the transition, so close it once it's shown).
    $(document).on('shown.bs.modal', '.modal', function () { $(this).data('snShown', true); })
        .on('hide.bs.modal', '.modal', function () { $(this).data('snShown', false); });
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape' || e.defaultPrevented) return;
        const top = $('.modal.show').last()[0];
        const instance = top && bootstrap.Modal.getInstance(top);
        if (!instance) return;
        if (!$(top).data('snShown')) {
            e.preventDefault();
            $(top).one('shown.bs.modal', () => instance.hide());
        } else if (!top.contains(e.target)) {
            e.preventDefault();
            instance.hide();
        }
    });

    // Bootstrap doesn't stack modals: raise a dialog opened from another dialog (e.g. a prompt from Settings)
    // and its backdrop above the one underneath.
    $(document).on('show.bs.modal', '.modal', function () {
        const open = $('.modal.show').not(this).length;
        const z = 1055 + open * 20;
        $(this).css('z-index', z);
        setTimeout(() => $('.modal-backdrop').last().css('z-index', z - 5), 0);
    }).on('hidden.bs.modal', '.modal', function () {
        $(this).css('z-index', '');
        // Keep scrolling locked and hand focus back to the dialog underneath (so Esc and Tab keep working).
        const $top = $('.modal.show').last();
        if ($top.length) {
            document.body.classList.add('modal-open');
            if (!$top[0].contains(document.activeElement)) $top.trigger('focus');
        }
    });

    SN.ui = {
        COLORS, ICONS, EMOJI, isMac,
        toast, confirm, prompt, contextMenu, closeContextMenu, pickStyle,
        iconHtml, colorClass, isTyping, copyText, download, formatShortcut, api, modal,
    };
})(jQuery);
