/* SyncedNotes – sidebar: views, binders and tags (all of them are drop targets for notes). */
(function ($) {
    'use strict';
    const { util, store, ui } = SN;
    const esc = util.escapeHtml;

    function viewInfo(view) {
        switch (view.type) {
            case 'pinned': return { name: 'Pinned', icon: 'bi-pin-angle' };
            case 'unfiled': return { name: 'Unfiled', icon: 'bi-inbox' };
            case 'archive': return { name: 'Archive', icon: 'bi-archive' };
            case 'trash': return { name: 'Trash', icon: 'bi-trash3' };
            case 'tag': return { name: `#${view.id}`, icon: 'bi-tag' };
            case 'binder': {
                const b = store.getBinder(view.id);
                return b ? { name: b.name, icon: b.icon || 'bi-folder2', color: b.color } : { name: 'Binder', icon: 'bi-folder2' };
            }
            default: return { name: 'All notes', icon: 'bi-journal-text' };
        }
    }

    function render() {
        const c = store.counts();
        const view = store.state.view;

        $('#navViews [data-view]').each(function () {
            const v = this.dataset.view;
            $(this).toggleClass('active', view.type === v).attr('aria-current', view.type === v ? 'page' : null)
                .find('.count').text(c[v] || '');
        });

        const binders = store.listBinders();
        $('#navBinders').html(binders.length ? binders.map((b) => {
            const active = view.type === 'binder' && view.id === b.id;
            return `<li data-binder-id="${b.id}">
                <a class="nav-item binder-item ${ui.colorClass(b.color)}${active ? ' active' : ''}" href="#/binder/${b.id}" data-drop="binder:${b.id}"${active ? ' aria-current="page"' : ''}>
                    ${ui.iconHtml(b.icon, 'bi-folder2')}<span class="label">${esc(b.name)}</span><span class="count">${c.binders[b.id] || ''}</span>
                    <button type="button" class="btn btn-icon btn-sm nav-more" data-binder-menu="${b.id}" aria-label="Options for ${esc(b.name)}"><i class="bi bi-three-dots"></i></button>
                </a></li>`;
        }).join('') : '<li class="nav-empty">No binders yet</li>');

        const tags = Object.values(c.tags).sort((a, b) => a.name.localeCompare(b.name));
        $('#navTags').html(tags.length ? tags.map((t) => {
            const active = view.type === 'tag' && String(view.id).toLowerCase() === t.name.toLowerCase();
            return `<li><a class="nav-item tag-item${active ? ' active' : ''}" href="#/tag/${encodeURIComponent(t.name)}" data-drop="tag:${esc(t.name)}" data-tag="${esc(t.name)}">
                <i class="bi bi-hash"></i><span class="label">${esc(t.name)}</span><span class="count">${t.count}</span></a></li>`;
        }).join('') : '<li class="nav-empty">Tags you add to notes appear here</li>');

        configureBinderSorting();
        renderTitle();
    }

    function renderTitle(total) {
        const info = viewInfo(store.state.view);
        if (total === undefined) total = store.visibleNotes().total;
        $('#viewTitle').attr('class', `pane-title ${ui.colorClass(info.color)}`)
            .html(`${ui.iconHtml(info.icon)}<span class="text-truncate">${esc(info.name)}</span><span class="count">${total || ''}</span>`);
        document.title = `${info.name} · SyncedNotes`;
    }

    function configureBinderSorting() {
        const el = document.getElementById('navBinders');
        if (!window.Sortable || Sortable.get(el)) return;
        Sortable.create(el, {
            group: { name: 'binders', pull: false, put: false },
            animation: 150,
            forceFallback: true,
            fallbackTolerance: 5,
            delay: 250,
            delayOnTouchOnly: true,
            filter: '.nav-more, .nav-empty',
            preventOnFilter: false,
            draggable: 'li[data-binder-id]',
            ghostClass: 'sortable-ghost',
            onEnd() {
                const ids = Array.from(el.querySelectorAll('li[data-binder-id]')).map((li) => li.dataset.binderId);
                const patches = new Map();
                ids.forEach((id, i) => {
                    const b = store.getBinder(id);
                    if (b && b.sortOrder !== i) patches.set(id, { sortOrder: i });
                });
                if (patches.size) store.updateBinders(patches); else render();
            },
        });
    }

    function closeOnMobile() {
        if (window.matchMedia('(max-width: 991.98px)').matches) {
            const oc = bootstrap.Offcanvas.getInstance(document.getElementById('sidebar'));
            if (oc) oc.hide();
        }
    }

    function bind() {
        const $side = $('#sidebar');
        $side.on('click', '.nav-item', function (e) {
            if ($(e.target).closest('[data-binder-menu]').length) { e.preventDefault(); return; }
            closeOnMobile();
        }).on('click', '[data-binder-menu]', function (e) {
            e.preventDefault();
            e.stopPropagation();
            const r = this.getBoundingClientRect();
            SN.commands.binderMenu(String($(this).data('binderMenu')), { x: r.left, y: r.bottom + 4 });
        }).on('contextmenu', '.binder-item', function (e) {
            e.preventDefault();
            SN.commands.binderMenu(this.closest('li').dataset.binderId, { x: e.clientX, y: e.clientY });
        }).on('contextmenu', '.tag-item', function (e) {
            e.preventDefault();
            SN.commands.tagMenu(String(this.dataset.tag), { x: e.clientX, y: e.clientY });
        });

        // Native drag & drop from other apps isn't handled here; note drags are tracked by the list.
        store.on('notes', () => render());
        store.on('binders', () => render());
        store.on('view', () => render());
    }

    function init() {
        bind();
        render();
    }

    SN.sidebar = { init, render, renderTitle, viewInfo };
})(jQuery);
