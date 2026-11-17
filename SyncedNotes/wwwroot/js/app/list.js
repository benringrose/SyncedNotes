/* SyncedNotes – the note list (list rows or card grid), keyboard navigation and drag & drop. */
(function ($) {
    'use strict';
    const { util, store, ui } = SN;
    const esc = util.escapeHtml;

    let listEl, scrollEl, grpPinned, grpOthers, secPinned, labelOthers;
    const cache = new Map();
    let drag = null;

    // ------------------------------------------------------------------ HTML
    function searchWords() {
        return String(store.state.search || '').toLowerCase().split(/\s+/).filter((w) => w && !w.startsWith('#'));
    }

    function highlight(text, words) {
        if (!words.length) return esc(text);
        const re = new RegExp(`(${words.map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi');
        return String(text).split(re).map((part, i) => (i % 2 ? `<mark>${esc(part)}</mark>` : esc(part))).join('');
    }

    // Snippets and thumbnails are memoised per note version: the list re-renders while you type, and stripping
    // Markdown from every (possibly huge) note each time would be wasteful.
    const memo = new Map();
    function derived(n, words) {
        const content = n.content || '';
        const key = `${n.updatedAt}|${content.length}|${words.join(' ')}`;
        const hit = memo.get(n.id);
        if (hit && hit.key === key) return hit;
        let snippet;
        const at = words.length ? content.toLowerCase().indexOf(words[0]) : -1;
        if (at > 120) {
            // Show the text around the first search match.
            snippet = '…' + util.stripMarkdown(content.slice(Math.max(0, at - 60), at + 600)).slice(0, 260);
        } else {
            snippet = util.stripMarkdown(content.slice(0, 1200)).slice(0, 260);
        }
        // Restricted character set: the URL is placed inside an inline url('…').
        const m = /!\[[^\]]*\]\((\/attachments\/[0-9a-f-]{36}|https:\/\/[\w.~:/?#@!$&*+,;=%-]+)\)/i.exec(content.slice(0, 20000));
        const entry = { key, snippet, image: m ? m[1] : null };
        memo.set(n.id, entry);
        return entry;
    }

    const snippetText = (n, words) => derived(n, words).snippet;

    function firstImage(n) {
        const image = derived(n, searchWords()).image;
        if (!image) return null;
        if (image.startsWith('/attachments/')) return SN.attachments.localUrl(image.split('/').pop()) || image;
        return image;
    }

    function dateFor(n) {
        const view = store.state.view.type;
        if (view === 'trash') return { ms: n.trashedAt, label: 'Deleted' };
        if (store.state.prefs.sort === 'created') return { ms: n.createdAt, label: 'Created' };
        return { ms: n.updatedAt, label: 'Edited' };
    }

    function itemHtml(n, grid) {
        const words = searchWords();
        const selected = n.id === store.state.selectedId;
        const binder = n.binderId && store.state.view.type !== 'binder' ? store.getBinder(n.binderId) : null;
        const colorCls = n.color ? ` has-color ${ui.colorClass(n.color)}` : '';
        const d = dateFor(n);
        const title = n.title || util.deriveTitle(n.content) || 'Untitled';
        const binderChip = binder ? `<span class="tag-chip binder-chip ${ui.colorClass(binder.color)}">${ui.iconHtml(binder.icon, 'bi-folder2')} ${esc(binder.name)}</span>` : '';
        const tagLimit = grid ? (binder ? 1 : 2) : 3;
        const tags = (n.tags || []).slice(0, tagLimit).map((t) => `<span class="tag-chip">#${esc(t)}</span>`).join('');
        const menuBtn = '<button type="button" class="btn btn-icon btn-sm item-menu-btn" data-item-menu tabindex="-1" aria-label="Note actions"><i class="bi bi-three-dots"></i></button>';
        const common = `data-id="${n.id}" role="option" aria-selected="${selected}" tabindex="${selected ? 0 : -1}"`;
        const showSnippet = store.state.prefs.showSnippets !== false;

        if (grid) {
            const img = firstImage(n);
            return `<div class="note-card${colorCls}${selected ? ' selected' : ''}" ${common}>
                <div class="nc-head"><span class="nc-icon">${ui.iconHtml(n.icon)}</span><div class="nc-title">${highlight(title, words)}</div>${n.pinned ? '<i class="bi bi-pin-angle-fill nc-pin" title="Pinned"></i>' : ''}</div>
                <div class="nc-body${img ? ' has-image' : ''}">${highlight(snippetText(n, words), words)}</div>
                ${img ? `<div class="nc-thumb" style="background-image:url('${esc(img)}')"></div>` : ''}
                <div class="nc-foot"><div class="nc-chips">${binderChip}${tags}</div><span class="nc-date" title="${d.label} ${esc(util.formatDateTime(d.ms))}">${esc(util.formatRelative(d.ms, true))}</span></div>
                ${menuBtn}</div>`;
        }
        return `<div class="note-item${colorCls}${selected ? ' selected' : ''}" ${common}>
            <span class="ni-icon">${ui.iconHtml(n.icon, n.pinned ? 'bi-pin-angle' : 'bi-file-earmark-text')}</span>
            <div class="ni-title">${highlight(title, words)}</div>
            <div class="ni-meta">${n.pinned && n.icon ? '<i class="bi bi-pin-angle-fill" title="Pinned"></i>' : ''}<span title="${d.label} ${esc(util.formatDateTime(d.ms))}">${esc(util.formatRelative(d.ms))}</span></div>
            ${showSnippet ? `<div class="ni-snippet">${highlight(snippetText(n, words), words) || '<em>No content</em>'}</div>` : ''}
            <div class="ni-tags">${binderChip}${tags}</div>
            ${menuBtn}</div>`;
    }

    function elementFrom(html) {
        const tpl = document.createElement('template');
        tpl.innerHTML = html.trim();
        return tpl.content.firstElementChild;
    }

    /** Keyed reconciliation: only rebuilds items whose HTML changed and moves the rest. */
    function renderGroup(container, notes, grid) {
        notes.forEach((n, i) => {
            const html = itemHtml(n, grid);
            let entry = cache.get(n.id);
            if (!entry || entry.html !== html) {
                const el = elementFrom(html);
                if (entry && entry.el.parentNode) entry.el.replaceWith(el);
                entry = { html, el };
                cache.set(n.id, entry);
            }
            const current = container.children[i];
            if (current !== entry.el) container.insertBefore(entry.el, current || null);
        });
        while (container.children.length > notes.length) container.lastElementChild.remove();
    }

    // ------------------------------------------------------------------ Empty states & banners
    function emptyHtml() {
        const { view, search } = store.state;
        if (search) return `<i class="bi bi-search"></i><h2>No matching notes</h2><p>Nothing matches “${esc(search)}”.</p>`;
        const newBtn = '<button type="button" class="btn btn-primary btn-sm mt-2" data-cmd="note.new"><i class="bi bi-plus-lg"></i> New note</button>';
        switch (view.type) {
            case 'pinned': return '<i class="bi bi-pin-angle"></i><h2>No pinned notes</h2><p>Pin important notes to keep them at the top.</p>';
            case 'archive': return '<i class="bi bi-archive"></i><h2>Archive is empty</h2><p>Archive notes you want to keep but don\'t need to see every day.</p>';
            case 'trash': return '<i class="bi bi-trash3"></i><h2>Trash is empty</h2><p>Deleted notes stay here for 30 days.</p>';
            case 'unfiled': return '<i class="bi bi-inbox"></i><h2>No unfiled notes</h2><p>Notes that aren\'t in a binder appear here.</p>';
            case 'binder': return `<i class="bi bi-folder2-open"></i><h2>This binder is empty</h2><p>Create a note here or drag notes onto the binder.</p>${newBtn}`;
            case 'tag': return `<i class="bi bi-tag"></i><h2>No notes tagged #${esc(view.id)}</h2>`;
            default: return `<i class="bi bi-journal-plus"></i><h2>No notes yet</h2><p>Press <kbd>Alt</kbd> <kbd>N</kbd> to write your first note.</p>${newBtn}`;
        }
    }

    function bannerHtml(total) {
        const view = store.state.view.type;
        if (view === 'trash' && total) {
            return `<div class="list-banner"><span><i class="bi bi-info-circle"></i> Notes in the trash are deleted after 30 days.</span>
                <button type="button" class="btn btn-sm btn-outline-danger" data-cmd="trash.empty">Empty trash</button></div>`;
        }
        if (store.state.prefs.sort === 'manual' && store.state.search && total > 1) {
            return '<div class="list-banner"><span><i class="bi bi-arrows-move"></i> Clear the search to rearrange notes.</span></div>';
        }
        return '';
    }

    // ------------------------------------------------------------------ Render
    function manualSortEnabled() {
        const { prefs, view, search } = store.state;
        return prefs.sort === 'manual' && !search && !['trash', 'archive'].includes(view.type);
    }

    function render() {
        if (!listEl) return;
        const grid = store.state.prefs.viewMode === 'grid';
        listEl.classList.toggle('grid', grid);
        document.body.dataset.viewMode = grid ? 'grid' : 'list';
        const { pinned, others, total } = store.visibleNotes();
        secPinned.hidden = !pinned.length;
        labelOthers.hidden = !pinned.length || !others.length;
        renderGroup(grpPinned, pinned, grid);
        renderGroup(grpOthers, others, grid);
        $('#listEmpty').prop('hidden', total > 0).html(total ? '' : emptyHtml());
        $('#listBanner').html(bannerHtml(total));
        configureSortables();
        if (SN.sidebar) SN.sidebar.renderTitle(total);
    }

    function ensureStructure() {
        listEl.innerHTML = `
            <div class="note-section" id="secPinned" hidden>
                <div class="section-label"><i class="bi bi-pin-angle-fill"></i> Pinned</div>
                <div class="note-group" id="grpPinned" data-group="pinned"></div>
            </div>
            <div class="note-section" id="secOthers">
                <div class="section-label" id="labelOthers" hidden>Notes</div>
                <div class="note-group" id="grpOthers" data-group="others"></div>
            </div>`;
        secPinned = document.getElementById('secPinned');
        labelOthers = document.getElementById('labelOthers');
        grpPinned = document.getElementById('grpPinned');
        grpOthers = document.getElementById('grpOthers');
    }

    // ------------------------------------------------------------------ Drag & drop
    function configureSortables() {
        if (!window.Sortable) return;
        const manual = manualSortEnabled();
        [grpPinned, grpOthers].forEach((el) => {
            let s = Sortable.get(el);
            if (!s) {
                s = Sortable.create(el, {
                    group: { name: 'notes', pull: true, put: true },
                    animation: 150,
                    forceFallback: true,
                    fallbackOnBody: true,
                    fallbackTolerance: 5,
                    delay: 220,
                    delayOnTouchOnly: true,
                    touchStartThreshold: 6,
                    ghostClass: 'sortable-ghost',
                    chosenClass: 'sortable-chosen',
                    dragClass: 'sortable-drag',
                    filter: '.item-menu-btn',
                    preventOnFilter: false,
                    scroll: true,
                    bubbleScroll: true,
                    onStart,
                    onEnd,
                });
            }
            s.option('sort', manual);
            s.option('group', { name: 'notes', pull: true, put: manual });
        });
    }

    function onStart(evt) {
        drag = { id: evt.item.dataset.id, target: null };
        document.body.classList.add('dragging', 'dragging-note');
        document.addEventListener('pointermove', trackDrag, true);
        document.addEventListener('touchmove', trackDrag, { capture: true, passive: true });
        document.addEventListener('mousemove', trackDrag, true);
    }

    function trackDrag(e) {
        if (!drag) return;
        const pt = e.touches ? e.touches[0] : e;
        if (!pt) return;
        const el = document.elementFromPoint(pt.clientX, pt.clientY);
        const target = el ? el.closest('#sidebar [data-drop]') : null;
        if (target !== drag.target) {
            if (drag.target) drag.target.classList.remove('drop-hover');
            if (target) target.classList.add('drop-hover');
            drag.target = target;
        }
    }

    function endDrag() {
        document.removeEventListener('pointermove', trackDrag, true);
        document.removeEventListener('touchmove', trackDrag, { capture: true });
        document.removeEventListener('mousemove', trackDrag, true);
        document.body.classList.remove('dragging', 'dragging-note');
        if (drag && drag.target) drag.target.classList.remove('drop-hover');
    }

    function onEnd(evt) {
        const state = drag;
        endDrag();
        drag = null;
        if (!state) return;
        const id = state.id;
        if (state.target) {
            render(); // undo Sortable's DOM move
            SN.commands.dropNote([id], state.target.dataset.drop);
            return;
        }
        if (!manualSortEnabled() || (evt.from === evt.to && evt.oldIndex === evt.newIndex)) { render(); return; }

        const items = Array.from(evt.to.children);
        const idx = items.indexOf(evt.item);
        const prev = idx > 0 ? store.getNote(items[idx - 1].dataset.id) : null;
        const next = idx < items.length - 1 ? store.getNote(items[idx + 1].dataset.id) : null;
        const note = store.getNote(id);
        if (!note) { render(); return; }

        const toPinned = evt.to === grpPinned;
        let order;
        if (prev && next) order = (prev.sortOrder + next.sortOrder) / 2;
        else if (prev) order = prev.sortOrder + 1;
        else if (next) order = next.sortOrder - 1;
        else order = note.sortOrder;

        if (prev && next && Math.abs(next.sortOrder - prev.sortOrder) < 1e-9) {
            // Ran out of room between neighbours: renumber the whole group in its new order.
            const ids = items.map((el) => el.dataset.id);
            SN.commands.reorder(ids, ids.map((_, i) => i), toPinned);
            return;
        }
        SN.commands.reorder([id], [order], toPinned);
    }

    // ------------------------------------------------------------------ Selection & keyboard
    function items() { return Array.from(listEl.querySelectorAll('[data-id]')); }

    function focusSelected() {
        const all = items();
        const el = all.find((x) => x.dataset.id === store.state.selectedId) || all[0];
        if (el) { el.focus({ preventScroll: false }); el.scrollIntoView({ block: 'nearest' }); }
    }

    function moveSelection(delta) {
        const all = items();
        if (!all.length) return;
        let idx = all.findIndex((x) => x === document.activeElement || x.dataset.id === store.state.selectedId);
        idx = idx < 0 ? 0 : util.clamp(idx + delta, 0, all.length - 1);
        const el = all[idx];
        SN.app.openNote(el.dataset.id, { replace: true });
        requestAnimationFrame(() => {
            const fresh = items().find((x) => x.dataset.id === el.dataset.id);
            if (fresh) { fresh.focus(); fresh.scrollIntoView({ block: 'nearest' }); }
        });
    }

    function gridColumns() {
        const all = items();
        if (all.length < 2 || !listEl.classList.contains('grid')) return 1;
        const top = all[0].getBoundingClientRect().top;
        let n = 0;
        for (const el of all) { if (Math.abs(el.getBoundingClientRect().top - top) < 4) n++; else break; }
        return Math.max(1, n);
    }

    function bind() {
        $(listEl).on('click', '[data-id]', function (e) {
            if ($(e.target).closest('[data-item-menu]').length) return;
            SN.app.openNote(this.dataset.id);
        }).on('click', '[data-item-menu]', function (e) {
            e.stopPropagation();
            const r = this.getBoundingClientRect();
            SN.commands.noteMenu(this.closest('[data-id]').dataset.id, { x: r.left, y: r.bottom + 4 });
        }).on('contextmenu', '[data-id]', function (e) {
            e.preventDefault();
            SN.commands.noteMenu(this.dataset.id, { x: e.clientX, y: e.clientY });
        }).on('keydown', '[data-id]', function (e) {
            const grid = listEl.classList.contains('grid');
            const cols = grid ? gridColumns() : 1;
            switch (e.key) {
                case 'ArrowDown': e.preventDefault(); moveSelection(cols); break;
                case 'ArrowUp': e.preventDefault(); moveSelection(-cols); break;
                case 'ArrowRight': if (grid) { e.preventDefault(); moveSelection(1); } break;
                case 'ArrowLeft': if (grid) { e.preventDefault(); moveSelection(-1); } break;
                case 'Home': e.preventDefault(); moveSelection(-100000); break;
                case 'End': e.preventDefault(); moveSelection(100000); break;
                case 'Enter':
                    e.preventDefault();
                    SN.app.openNote(this.dataset.id, { focus: 'content' });
                    break;
                case 'Delete':
                    e.preventDefault();
                    SN.commands.run(store.getNote(this.dataset.id) && store.getNote(this.dataset.id).trashedAt ? 'note.deleteForever' : 'note.trash', { id: this.dataset.id });
                    break;
                case 'ContextMenu': {
                    e.preventDefault();
                    const r = this.getBoundingClientRect();
                    SN.commands.noteMenu(this.dataset.id, { x: r.left + 20, y: r.top + 20 });
                    break;
                }
                default: break;
            }
        });

        store.on('notes', () => render());
        store.on('binders', () => { cache.clear(); render(); });
        store.on('view', () => { scrollEl.scrollTop = 0; render(); });
        store.on('select', () => render());
        store.on('prefs', () => render());
        // Relative dates ("5 minutes ago") refresh once a minute.
        setInterval(() => { cache.clear(); render(); }, 60_000);
    }

    function init() {
        listEl = document.getElementById('noteList');
        scrollEl = document.getElementById('noteListScroll');
        ensureStructure();
        bind();
    }

    SN.list = { init, render, focusSelected, manualSortEnabled };
})(jQuery);
