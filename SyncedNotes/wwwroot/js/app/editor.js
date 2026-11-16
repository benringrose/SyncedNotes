/* SyncedNotes – the note editor (Markdown textarea + live preview). */
(function ($) {
    'use strict';
    const { util, store, ui, markdown } = SN;
    const esc = util.escapeHtml;

    let ta, titleEl, preview, previewScroll, bodyEl, $editor, $empty;
    let noteId = null;
    let mode = 'split';
    let readOnly = false;
    let lastSaved = { title: '', content: '' };
    let pendingSel = null;
    let pendingKind = null;
    let syncingScroll = false;
    let previewStale = true;
    const histories = new Map();

    const persistSoon = util.debounce(() => persist(), 450, 3000);
    const previewSoon = util.debounce(() => renderPreview(), 140, 700);
    const countsSoon = util.debounce(() => updateCounts(), 300, 1500);

    const hist = () => {
        let h = histories.get(noteId);
        if (!h) { h = new SN.TextHistory(ta.value, sel()); histories.set(noteId, h); }
        return h;
    };
    const sel = () => ({ start: ta.selectionStart, end: ta.selectionEnd });
    const isSmallScreen = () => window.matchMedia('(max-width: 767.98px)').matches;

    // ------------------------------------------------------------------ Open / close
    function open(id, opts) {
        opts = opts || {};
        if (noteId && noteId !== id) flush();
        const note = store.getNote(id);
        if (!note) { close(); return false; }
        const switching = noteId !== id;
        noteId = id;
        store.select(id);
        $empty.prop('hidden', true);
        $editor.prop('hidden', false);
        document.body.classList.add('note-open');

        if (switching || (!persistSoon.pending() && (note.content !== ta.value || note.title !== titleEl.value))) {
            titleEl.value = note.title || '';
            ta.value = note.content || '';
            lastSaved = { title: titleEl.value, content: ta.value };
            const h = histories.get(id);
            if (!h) histories.set(id, new SN.TextHistory(ta.value, { start: 0, end: 0 }));
            else if (h.current.value !== ta.value) h.record(ta.value, sel(), sel(), 'external');
            ta.scrollTop = 0;
            previewScroll.scrollTop = 0;
            if (switching) ta.setSelectionRange(0, 0);
        }

        applyReadOnly(note);
        renderTags(note);
        updateChrome(note);
        previewStale = true;
        if (mode !== 'edit') renderPreview();
        updateCounts();
        updateUndoButtons();
        setSaveStatus(note.dirty && note.dirty.length ? 'pending' : 'synced');

        if (opts.focus === 'title') { titleEl.focus(); titleEl.select(); }
        else if (opts.focus === 'content' && !readOnly) {
            if (mode === 'preview') setMode(isSmallScreen() ? 'edit' : 'split');
            ta.focus();
            if (opts.caret === 'end') ta.setSelectionRange(ta.value.length, ta.value.length);
        }
        return true;
    }

    function close() {
        flush();
        noteId = null;
        $editor.prop('hidden', true);
        $empty.prop('hidden', false);
        document.body.classList.remove('note-open');
        store.select(null);
    }

    /** Writes pending edits to the offline store immediately. */
    function flush() {
        if (!persistSoon.pending()) return Promise.resolve();
        persistSoon.cancel();
        return persist();
    }

    async function persist() {
        if (!noteId || readOnly) return;
        const id = noteId;
        const title = titleEl.value;
        const content = ta.value;
        const patch = {};
        if (title !== lastSaved.title) patch.title = title;
        if (content !== lastSaved.content) patch.content = content;
        if (!Object.keys(patch).length) return;
        lastSaved = { title, content };
        setSaveStatus('saving');
        await store.updateNote(id, patch, 'editor');
        if (id === noteId) setSaveStatus(navigator.onLine ? 'pending' : 'offline');
    }

    // ------------------------------------------------------------------ Chrome (header, banners, tags)
    function applyReadOnly(note) {
        readOnly = !!note.trashedAt;
        ta.readOnly = readOnly;
        titleEl.readOnly = readOnly;
        $('#tagInput').prop('hidden', readOnly);
        $('#mdToolbar .tb').prop('disabled', readOnly);
        $editor.toggleClass('read-only', readOnly);
    }

    function updateChrome(note) {
        note = note || store.getNote(noteId);
        if (!note) return;
        const binder = note.binderId ? store.getBinder(note.binderId) : null;
        const $bp = $('#binderPicker');
        $bp.find('.label').text(binder ? binder.name : 'Unfiled');
        $bp.find('> .bi').first().attr('class', 'bi ' + (binder && binder.icon && binder.icon.startsWith('bi-') ? binder.icon : binder ? 'bi-folder2' : 'bi-inbox'));
        $bp.attr('class', `btn-chip ${binder ? ui.colorClass(binder.color) : ''}`);

        $('#btnPin').toggleClass('active', !!note.pinned).attr('aria-pressed', !!note.pinned)
            .attr('title', note.pinned ? 'Unpin (Alt+P)' : 'Pin (Alt+P)')
            .find('.bi').attr('class', note.pinned ? 'bi bi-pin-angle-fill' : 'bi bi-pin-angle');
        $('#btnArchive').toggleClass('active', !!note.archived).attr('title', note.archived ? 'Unarchive (Alt+A)' : 'Archive (Alt+A)')
            .find('.bi').attr('class', note.archived ? 'bi bi-archive-fill' : 'bi bi-archive');
        $('[data-archive-label]').text(note.archived ? 'Unarchive' : 'Archive');

        $('#noteIconBtn').html(ui.iconHtml(note.icon, 'bi-file-earmark-text'));
        $editor.attr('class', `editor ${note.color ? 'has-color ' + ui.colorClass(note.color) : ''} ${mode === 'preview' ? 'mode-preview' : ''} ${readOnly ? 'read-only' : ''}`.trim());

        let banner = '';
        if (note.trashedAt) {
            banner = `<div class="editor-banner trash"><i class="bi bi-trash3"></i><span>This note is in the trash and will be deleted after 30 days.</span>
                <span class="ms-auto"><button type="button" class="btn btn-sm btn-outline-secondary" data-cmd="note.restore">Restore</button>
                <button type="button" class="btn btn-sm btn-danger" data-cmd="note.deleteForever">Delete forever</button></span></div>`;
        } else if (note.archived) {
            banner = `<div class="editor-banner archive"><i class="bi bi-archive"></i><span>Archived</span>
                <span class="ms-auto"><button type="button" class="btn btn-sm btn-outline-secondary" data-cmd="note.archive">Unarchive</button></span></div>`;
        }
        $('#editorBanner').html(banner);
        $('#noteDates').text(`Created ${util.formatDateTime(note.createdAt)} · Edited ${util.formatRelative(note.updatedAt)}`)
            .attr('title', `Last edited ${util.formatDateTime(note.updatedAt)}`);
    }

    function renderTags(note) {
        note = note || store.getNote(noteId);
        if (!note) return;
        $('#tagChips').html((note.tags || []).map((t) =>
            `<span class="tag-chip">#${esc(t)}${readOnly ? '' : `<button type="button" data-remove-tag="${esc(t)}" aria-label="Remove tag ${esc(t)}"><i class="bi bi-x"></i></button>`}</span>`).join(''));
        const have = new Set((note.tags || []).map((t) => t.toLowerCase()));
        $('#tagSuggestions').html(store.allTags().filter((t) => !have.has(t.name.toLowerCase()))
            .map((t) => `<option value="${esc(t.name)}"></option>`).join(''));
    }

    function addTag(raw) {
        const tag = util.normalizeTag(raw);
        const note = store.getNote(noteId);
        if (!tag || !note || readOnly) return;
        if ((note.tags || []).some((t) => t.toLowerCase() === tag.toLowerCase())) return;
        if ((note.tags || []).length >= 30) { ui.toast('A note can have up to 30 tags.', { type: 'warning' }); return; }
        store.updateNote(noteId, { tags: [...(note.tags || []), tag] });
    }

    function removeTag(tag) {
        const note = store.getNote(noteId);
        if (!note) return;
        store.updateNote(noteId, { tags: (note.tags || []).filter((t) => t !== tag) });
    }

    function setSaveStatus(state) {
        const map = {
            saving: 'Saving…',
            pending: 'Saved on this device',
            offline: 'Saved offline – will sync later',
            synced: SN.syncer.status === 'offline' ? 'Offline – saved on this device' : 'Saved',
        };
        $('#saveStatus').text(map[state] || '');
    }

    function updateCounts() {
        const text = ta.value;
        const words = util.wordCount(util.stripMarkdown(text));
        const minutes = Math.max(1, Math.round(words / 220));
        $('#wordCount').text(`${words.toLocaleString()} word${words === 1 ? '' : 's'} · ${text.length.toLocaleString()} characters · ${minutes} min read`);
    }

    function updateUndoButtons() {
        if (!noteId) return;
        const h = hist();
        $('#mdToolbar [data-md=undo]').prop('disabled', readOnly || !h.canUndo());
        $('#mdToolbar [data-md=redo]').prop('disabled', readOnly || !h.canRedo());
    }

    // ------------------------------------------------------------------ Modes & preview
    function setMode(next, save) {
        if (!['edit', 'split', 'preview'].includes(next)) next = 'split';
        if (next === 'split' && isSmallScreen()) next = 'edit';
        mode = next;
        bodyEl.dataset.mode = next;
        $editor.toggleClass('mode-preview', next === 'preview');
        $('.mode-toggle [data-mode]').each(function () {
            $(this).toggleClass('active', $(this).data('mode') === next).attr('aria-pressed', $(this).data('mode') === next);
        });
        if (next !== 'edit' && previewStale) renderPreview();
        if (save) store.setPrefs({ editorMode: next });
    }

    function cycleMode() {
        const order = isSmallScreen() ? ['edit', 'preview'] : ['edit', 'split', 'preview'];
        setMode(order[(order.indexOf(mode) + 1) % order.length], true);
        if (mode !== 'preview') ta.focus();
    }

    function renderPreview() {
        if (!noteId) return;
        if (mode === 'edit') { previewStale = true; return; }
        preview.innerHTML = markdown.render(ta.value);
        markdown.enhance(preview);
        previewStale = false;
    }

    // ------------------------------------------------------------------ Editing primitives
    function changed() {
        setSaveStatus('saving');
        persistSoon();
        previewStale = true;
        if (mode !== 'edit') previewSoon();
        countsSoon();
        updateUndoButtons();
    }

    /** Replaces [start, end) with text as a single undo step and selects [selStart, selEnd). */
    function edit(start, end, text, selStart, selEnd, opts) {
        if (readOnly) return;
        const before = sel();
        ta.setRangeText(text, start, end, 'end');
        const s = selStart === undefined ? start + text.length : selStart;
        const e = selEnd === undefined ? s : selEnd;
        ta.setSelectionRange(s, e);
        if (!opts || opts.focus !== false) ta.focus();
        hist().breakGroup();
        hist().record(ta.value, before, { start: s, end: e }, 'command');
        changed();
    }

    function replaceAll(value, opts) {
        edit(0, ta.value.length, value, Math.min(ta.selectionStart, value.length), Math.min(ta.selectionEnd, value.length), opts);
    }

    function applyHistoryState(state) {
        if (!state) return;
        ta.value = state.value;
        ta.setSelectionRange(Math.min(state.sel.start, ta.value.length), Math.min(state.sel.end, ta.value.length));
        ta.blur();
        ta.focus(); // scrolls the caret into view
        changed();
    }

    function undo() { if (noteId && !readOnly) applyHistoryState(hist().undo()); }
    function redo() { if (noteId && !readOnly) applyHistoryState(hist().redo()); }

    function lineBounds(v, pos) {
        const start = v.lastIndexOf('\n', pos - 1) + 1;
        const nl = v.indexOf('\n', pos);
        return { start, end: nl < 0 ? v.length : nl };
    }

    function selectedLines() {
        const v = ta.value;
        const s = ta.selectionStart;
        let e = ta.selectionEnd;
        if (e > s && v[e - 1] === '\n') e--;
        const start = v.lastIndexOf('\n', s - 1) + 1;
        const nl = v.indexOf('\n', e);
        const end = nl < 0 ? v.length : nl;
        return { start, end, text: v.slice(start, end) };
    }

    function inFence(v, pos) {
        let fence = null;
        const lines = v.slice(0, pos).split('\n');
        lines.pop();
        for (const line of lines) {
            const m = /^\s*(```+|~~~+)/.exec(line);
            if (!m) continue;
            if (!fence) fence = m[1][0]; else if (m[1][0] === fence) fence = null;
        }
        return !!fence;
    }

    function wrap(before, after, placeholder) {
        const v = ta.value, s = ta.selectionStart, e = ta.selectionEnd;
        const selected = v.slice(s, e);
        if (v.slice(s - before.length, s) === before && v.slice(e, e + after.length) === after) {
            edit(s - before.length, e + after.length, selected, s - before.length, e - before.length);
            return;
        }
        if (selected.length >= before.length + after.length && selected.startsWith(before) && selected.endsWith(after)) {
            const inner = selected.slice(before.length, selected.length - after.length);
            edit(s, e, inner, s, s + inner.length);
            return;
        }
        const text = selected || placeholder;
        edit(s, e, before + text + after, s + before.length, s + before.length + text.length);
    }

    const LINE_PATTERNS = {
        task: /^(\s*)[-*+]\s+\[[ xX]\]\s+/,
        ul: /^(\s*)[-*+]\s+(?!\[[ xX]\]\s)/,
        ol: /^(\s*)\d+[.)]\s+/,
        quote: /^(\s*)>\s?/,
    };

    function toggleLines(kind) {
        const r = selectedLines();
        const lines = r.text.split('\n');
        let out;
        if (/^h[1-6]$/.test(kind)) {
            const level = Number(kind[1]);
            out = lines.map((l) => {
                const m = /^(#{1,6})\s+/.exec(l);
                if (m && m[1].length === level) return l.slice(m[0].length);
                return '#'.repeat(level) + ' ' + (m ? l.slice(m[0].length) : l);
            });
        } else {
            const re = LINE_PATTERNS[kind];
            const nonEmpty = lines.filter((l) => l.trim());
            const allHave = nonEmpty.length > 0 && nonEmpty.every((l) => re.test(l));
            let n = 1;
            out = lines.map((l) => {
                if (!l.trim() && lines.length > 1) return l;
                if (allHave) return l.replace(re, '$1');
                let stripped = l;
                for (const p of Object.values(LINE_PATTERNS)) stripped = stripped.replace(p, '$1');
                const indent = /^\s*/.exec(stripped)[0];
                const marker = kind === 'ul' ? '- ' : kind === 'ol' ? `${n++}. ` : kind === 'task' ? '- [ ] ' : '> ';
                return indent + marker + stripped.slice(indent.length);
            });
        }
        const text = out.join('\n');
        if (lines.length === 1) {
            const pos = Math.max(r.start, ta.selectionStart + (text.length - r.text.length));
            edit(r.start, r.end, text, Math.min(pos, r.start + text.length), Math.min(pos, r.start + text.length));
        } else {
            edit(r.start, r.end, text, r.start, r.start + text.length);
        }
    }

    function insertBlock(text, selectFrom, selectTo) {
        const v = ta.value, s = ta.selectionStart, e = ta.selectionEnd;
        const needsLeadingBreak = s > 0 && v[s - 1] !== '\n';
        const prefix = needsLeadingBreak ? '\n\n' : (s > 1 && v[s - 2] !== '\n' && s > 0 ? '\n' : '');
        const suffix = v[e] && v[e] !== '\n' ? '\n' : '';
        const full = prefix + text + suffix;
        const base = s + prefix.length;
        edit(s, e, full, base + (selectFrom ?? text.length), base + (selectTo ?? selectFrom ?? text.length));
    }

    function format(action, arg) {
        if (!noteId || readOnly) return;
        const v = ta.value, s = ta.selectionStart, e = ta.selectionEnd;
        const selected = v.slice(s, e);
        switch (action) {
            case 'undo': return undo();
            case 'redo': return redo();
            case 'bold': return wrap('**', '**', 'bold text');
            case 'italic': return wrap('*', '*', 'italic text');
            case 'strike': return wrap('~~', '~~', 'strikethrough');
            case 'code':
                if (selected.includes('\n')) return format('codeblock');
                return wrap('`', '`', 'code');
            case 'h1': case 'h2': case 'h3': case 'ul': case 'ol': case 'task': case 'quote':
                return toggleLines(action);
            case 'codeblock': {
                const lang = arg || '';
                const inner = selected || '';
                const text = '```' + lang + '\n' + inner + '\n```';
                return insertBlock(text, 4 + lang.length, 4 + lang.length + inner.length);
            }
            case 'link': {
                if (/^https?:\/\/\S+$/.test(selected)) return edit(s, e, `[](${selected})`, s + 1, s + 1);
                const label = selected || 'link text';
                const text = `[${label}](https://)`;
                return edit(s, e, text, s + label.length + 3, s + label.length + 11);
            }
            case 'image': return $('#imageFileInput').val('').trigger('click');
            case 'table':
                return insertBlock('| Column 1 | Column 2 | Column 3 |\n| --- | --- | --- |\n|  |  |  |\n|  |  |  |', 2, 10);
            case 'hr': return insertBlock('---\n');
            default: return undefined;
        }
    }

    /** Enter inside a list or quote continues it; Enter on an empty item ends it. */
    function continueList() {
        const v = ta.value, s = ta.selectionStart;
        if (s !== ta.selectionEnd) return false;
        const { start, end } = lineBounds(v, s);
        const line = v.slice(start, end);
        const m = /^(\s*)((?:>\s?)+|[-*+]\s+\[[ xX]\]\s+|[-*+]\s+|(\d+)([.)])\s+)/.exec(line);
        if (!m || s < start + m[0].length || inFence(v, start)) return false;
        const rest = line.slice(m[0].length);
        if (!rest.trim() && s === end) {
            // Leave the list with a blank line so the next paragraph isn't swallowed by the last item.
            edit(start, end, '\n', start + 1, start + 1);
            return true;
        }
        let next;
        if (m[3]) next = `${m[1]}${parseInt(m[3], 10) + 1}${m[4]} `;
        else if (/\[[ xX]\]/.test(m[2])) next = m[1] + m[2].replace(/\[[xX]\]/, '[ ]');
        else next = m[1] + m[2];
        const insert = '\n' + next;
        edit(s, s, insert, s + insert.length, s + insert.length);
        return true;
    }

    function indent(outdent) {
        const v = ta.value, s = ta.selectionStart, e = ta.selectionEnd;
        const multiLine = v.slice(s, e).includes('\n');
        const { start, end } = lineBounds(v, s);
        const line = v.slice(start, end);
        const structured = /^\s*([-*+]|\d+[.)])\s/.test(line) || /^\s*>/.test(line);
        if (!outdent && !multiLine && !structured) {
            edit(s, e, '    ', s + 4, s + 4);
            return;
        }
        const r = selectedLines();
        const lines = r.text.split('\n');
        const out = lines.map((l) => (outdent ? l.replace(/^( {1,4}|\t)/, '') : '    ' + l));
        const text = out.join('\n');
        if (!multiLine) {
            const delta = out[0].length - lines[0].length;
            const pos = Math.max(r.start, s + delta);
            edit(r.start, r.end, text, pos, Math.max(pos, e + delta));
        } else {
            edit(r.start, r.end, text, r.start, r.start + text.length);
        }
    }

    // ------------------------------------------------------------------ Images
    function imageFilesFrom(dt) {
        if (!dt) return [];
        let files = Array.from(dt.files || []);
        if (!files.length && dt.items) {
            files = Array.from(dt.items).filter((i) => i.kind === 'file').map((i) => i.getAsFile()).filter(Boolean);
        }
        return files.filter((f) => f.type && f.type.startsWith('image/'));
    }

    async function insertImages(files) {
        if (!noteId || readOnly || !files.length) return;
        const items = await SN.attachments.add(files);
        if (!items.length) return;
        const v = ta.value, s = ta.selectionStart, e = ta.selectionEnd;
        const prefix = s > 0 && v[s - 1] !== '\n' ? '\n' : '';
        const text = prefix + items.map((i) => i.markdown).join('\n') + '\n';
        edit(s, e, text, s + text.length, s + text.length);
        ui.toast(items.length === 1 ? 'Image added' : `${items.length} images added`, { type: 'success', timeout: 2000 });
    }

    // ------------------------------------------------------------------ Events
    function bind() {
        ta.addEventListener('beforeinput', (e) => {
            if (e.inputType === 'historyUndo') { e.preventDefault(); undo(); return; }
            if (e.inputType === 'historyRedo') { e.preventDefault(); redo(); return; }
            pendingSel = sel();
            if (e.inputType === 'insertText' || e.inputType === 'insertCompositionText') {
                if (/\s/.test(e.data || '')) hist().breakGroup(); // "hello| world" → undo word by word
                pendingKind = 'insert';
            } else if (e.inputType && e.inputType.startsWith('delete')) {
                pendingKind = 'delete';
            } else {
                pendingKind = 'other';
            }
        });

        ta.addEventListener('input', () => {
            if (!noteId) return;
            hist().record(ta.value, pendingSel || sel(), sel(), pendingKind || 'insert');
            pendingSel = null;
            pendingKind = null;
            changed();
        });

        ta.addEventListener('keydown', (e) => {
            if (e.isComposing || e.keyCode === 229) return;
            const mod = e.ctrlKey || e.metaKey;
            const k = e.key.toLowerCase();
            if (mod && !e.altKey) {
                if (k === 'z' && !e.shiftKey) { e.preventDefault(); undo(); return; }
                if ((k === 'z' && e.shiftKey) || (k === 'y' && !e.shiftKey)) { e.preventDefault(); redo(); return; }
                if (!e.shiftKey && k === 'b') { e.preventDefault(); format('bold'); return; }
                if (!e.shiftKey && k === 'i') { e.preventDefault(); format('italic'); return; }
                if (!e.shiftKey && k === 'k') { e.preventDefault(); format('link'); return; }
                if (e.shiftKey && k === 'x') { e.preventDefault(); format('strike'); return; }
                if (!e.shiftKey && e.code === 'Backquote') { e.preventDefault(); format('code'); return; }
                if (e.shiftKey && e.code === 'Digit7') { e.preventDefault(); format('ol'); return; }
                if (e.shiftKey && e.code === 'Digit8') { e.preventDefault(); format('ul'); return; }
                if (e.shiftKey && e.code === 'Digit9') { e.preventDefault(); format('task'); return; }
            }
            if (e.key === 'Enter' && !mod && !e.altKey && !e.shiftKey) {
                if (continueList()) e.preventDefault();
                return;
            }
            if (e.key === 'Tab' && !mod && !e.altKey && !readOnly) {
                e.preventDefault();
                indent(e.shiftKey);
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                ta.blur();
                if (SN.list) SN.list.focusSelected();
            }
        });

        // Clicking or arrowing around starts a new undo step.
        ta.addEventListener('mousedown', () => hist().breakGroup());
        ta.addEventListener('keyup', (e) => { if (e.key.startsWith('Arrow') || e.key === 'Home' || e.key === 'End') hist().breakGroup(); });

        ta.addEventListener('paste', (e) => {
            const dt = e.clipboardData;
            const images = imageFilesFrom(dt);
            if (images.length) {
                e.preventDefault();
                insertImages(images);
                return;
            }
            const text = dt ? dt.getData('text/plain').trim() : '';
            if (/^https?:\/\/\S+$/.test(text) && ta.selectionStart !== ta.selectionEnd) {
                e.preventDefault();
                const s = ta.selectionStart, en = ta.selectionEnd;
                const label = ta.value.slice(s, en);
                edit(s, en, `[${label}](${text})`, s + label.length + text.length + 4, s + label.length + text.length + 4);
            }
        });

        titleEl.addEventListener('input', () => { if (noteId) { setSaveStatus('saving'); persistSoon(); } });
        titleEl.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || (e.key === 'ArrowDown' && titleEl.selectionStart === titleEl.value.length)) {
                e.preventDefault();
                if (mode === 'preview') setMode(isSmallScreen() ? 'edit' : 'split');
                ta.focus();
                ta.setSelectionRange(0, 0);
            } else if (e.key === 'Escape') {
                titleEl.blur();
                if (SN.list) SN.list.focusSelected();
            }
        });

        // Drag & drop images onto the editor.
        const hasImages = (dt) => dt && Array.from(dt.items || []).some((i) => i.kind === 'file' && /^image\//.test(i.type));
        $(bodyEl).on('dragover', (e) => {
            const dt = e.originalEvent.dataTransfer;
            if (!hasImages(dt) || readOnly) return;
            e.preventDefault();
            e.stopPropagation();
            dt.dropEffect = 'copy';
            bodyEl.classList.add('drag-over');
        }).on('dragleave', (e) => {
            if (!bodyEl.contains(e.originalEvent.relatedTarget)) bodyEl.classList.remove('drag-over');
        }).on('drop', (e) => {
            bodyEl.classList.remove('drag-over');
            const files = imageFilesFrom(e.originalEvent.dataTransfer);
            if (!files.length) return;
            e.preventDefault();
            e.stopPropagation();
            insertImages(files);
        });

        $('#imageFileInput').on('change', function () {
            insertImages(Array.from(this.files || []));
            this.value = '';
        });

        // Toolbar (keep the textarea focused so the selection is preserved).
        $('#mdToolbar').on('mousedown', '.tb:not([data-bs-toggle]), .dropdown-item[data-md]', (e) => e.preventDefault())
            .on('click', '[data-md]', function () { format($(this).data('md'), $(this).data('lang')); });
        $('#codeLangMenu').html(markdown.LANGUAGE_MENU.map(([id, name]) =>
            `<li><button type="button" class="dropdown-item" data-md="codeblock" data-lang="${id === 'plaintext' ? '' : id}">${esc(name)}</button></li>`).join(''));

        $('.mode-toggle').on('click', '[data-mode]', function () { setMode($(this).data('mode'), true); });

        // Preview interactions.
        $(preview).on('change', 'input[data-task]', function () {
            if (readOnly) { this.checked = !this.checked; return; }
            const next = markdown.toggleTask(ta.value, Number(this.dataset.index));
            if (next !== ta.value) replaceAll(next, { focus: false });
        }).on('click', '[data-copy-code]', async function () {
            const code = $(this).closest('.code-block').find('pre code').text();
            if (await ui.copyText(code)) {
                $(this).html('<i class="bi bi-check2"></i> Copied');
                setTimeout(() => $(this).html('<i class="bi bi-clipboard"></i> Copy'), 1500);
            }
        }).on('click', 'a.wikilink', async function (e) {
            e.preventDefault();
            const target = this.getAttribute('data-wikilink');
            const existing = store.findNoteByTitle(target);
            if (existing) { location.hash = `#/note/${existing.id}`; return; }
            const current = store.getNote(noteId);
            const created = await store.createNote({ title: target, binderId: current ? current.binderId : null });
            location.hash = `#/note/${created.id}`;
        }).on('dblclick', () => {
            if (mode === 'preview' && !readOnly) setMode(isSmallScreen() ? 'edit' : 'split', true);
        });

        // Keep editor and preview scrolled together in split view.
        const syncScroll = (from, to) => () => {
            if (mode !== 'split' || !store.state.prefs.syncScroll || syncingScroll) return;
            syncingScroll = true;
            const ratio = from.scrollTop / Math.max(1, from.scrollHeight - from.clientHeight);
            to.scrollTop = ratio * (to.scrollHeight - to.clientHeight);
            requestAnimationFrame(() => { syncingScroll = false; });
        };
        ta.addEventListener('scroll', syncScroll(ta, previewScroll), { passive: true });
        previewScroll.addEventListener('scroll', syncScroll(previewScroll, ta), { passive: true });

        // Tags.
        const $tagInput = $('#tagInput');
        $tagInput.on('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ',') {
                e.preventDefault();
                addTag($tagInput.val());
                $tagInput.val('');
            } else if (e.key === 'Backspace' && !$tagInput.val()) {
                const note = store.getNote(noteId);
                if (note && note.tags && note.tags.length) removeTag(note.tags[note.tags.length - 1]);
            } else if (e.key === 'Escape') {
                $tagInput.val('');
                ta.focus();
            }
        }).on('input', () => {
            // Picking a datalist suggestion fills the whole value at once.
            const v = String($tagInput.val() || '');
            if (store.allTags().some((t) => t.name === v)) { addTag(v); $tagInput.val(''); }
        }).on('blur', () => {
            if ($tagInput.val()) { addTag($tagInput.val()); $tagInput.val(''); }
        });
        $('#tagChips').on('click', '[data-remove-tag]', function () { removeTag(String($(this).data('removeTag'))); });

        // Binder picker.
        $('#binderPicker').closest('.dropdown').on('show.bs.dropdown', () => {
            const note = store.getNote(noteId);
            if (!note) return;
            const items = [{ id: '', name: 'Unfiled', icon: 'bi-inbox' }, ...store.listBinders()];
            $('#binderPickerMenu').html(items.map((b) =>
                `<li><button type="button" class="dropdown-item ${b.color ? 'has-color ' + ui.colorClass(b.color) : ''} ${(b.id || null) === (note.binderId || null) ? 'checked' : ''}" data-binder="${b.id}">
                    ${ui.iconHtml(b.icon, 'bi-folder2')}<span>${esc(b.name)}</span></button></li>`).join('') +
                '<li><hr class="dropdown-divider"></li><li><button type="button" class="dropdown-item" data-cmd="binder.new" data-assign-note="1"><i class="bi bi-plus-lg"></i>New binder…</button></li>');
        });
        $('#binderPickerMenu').on('click', '[data-binder]', function () {
            if (noteId) store.updateNote(noteId, { binderId: String($(this).data('binder')) || null });
        });

        // Keep the editor in sync with changes from elsewhere (other tabs, other devices, list actions).
        store.on('notes', ({ ids, source }) => {
            if (!noteId || !ids.includes(noteId) || source === 'editor') return;
            const note = store.getNote(noteId);
            if (!note) {
                if (source === 'remote') ui.toast('This note was deleted on another device.', { type: 'warning' });
                close();
                return;
            }
            applyReadOnly(note);
            updateChrome(note);
            renderTags(note);
            if (!persistSoon.pending() && (note.content !== ta.value || note.title !== titleEl.value)) {
                const before = sel();
                if (note.title !== titleEl.value && document.activeElement !== titleEl) titleEl.value = note.title;
                if (note.content !== ta.value) {
                    ta.value = note.content;
                    const s = Math.min(before.start, ta.value.length), e = Math.min(before.end, ta.value.length);
                    ta.setSelectionRange(s, e);
                    hist().breakGroup();
                    hist().record(ta.value, before, { start: s, end: e }, 'external');
                }
                lastSaved = { title: titleEl.value, content: ta.value };
                previewStale = true;
                renderPreview();
                updateCounts();
                updateUndoButtons();
            }
            if (source === 'remote') setSaveStatus(note.dirty && note.dirty.length ? 'pending' : 'synced');
        });
        store.on('binders', () => { if (noteId) updateChrome(); });
        store.on('sync', (s) => {
            const note = noteId && store.getNote(noteId);
            if (!note || persistSoon.pending()) return;
            if (s.status === 'offline') setSaveStatus('offline');
            else if (!(note.dirty && note.dirty.length)) setSaveStatus('synced');
        });

        // Save before the page is hidden or closed.
        document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') flush(); });
        window.addEventListener('pagehide', () => flush());
        window.matchMedia('(max-width: 767.98px)').addEventListener('change', () => setMode(store.state.prefs.editorMode));
    }

    function applyPrefs(prefs) {
        document.body.classList.toggle('font-serif', prefs.font === 'serif');
        document.body.classList.toggle('font-mono', prefs.font === 'mono');
        document.documentElement.style.setProperty('--editor-size', `${Number(prefs.fontSize) || 15}px`);
        ta.spellcheck = prefs.spellcheck !== false;
        $('#mdToolbar').prop('hidden', prefs.toolbar === false);
        setMode(prefs.editorMode || 'split');
    }

    function init() {
        ta = document.getElementById('noteContent');
        titleEl = document.getElementById('noteTitle');
        preview = document.getElementById('notePreview');
        previewScroll = document.getElementById('previewScroll');
        bodyEl = document.getElementById('editorBody');
        $editor = $('#editor');
        $empty = $('#editorEmpty');
        bind();
        applyPrefs(store.state.prefs);
        store.on('prefs', () => applyPrefs(store.state.prefs));
    }

    SN.editor = {
        init, open, close, flush, undo, redo, format, setMode, cycleMode, insertImages,
        currentId: () => noteId,
        mode: () => mode,
        focus(where) { if (!noteId) return; if (where === 'title') titleEl.focus(); else ta.focus(); },
        hasFocus: () => document.activeElement === ta || document.activeElement === titleEl,
        textarea: () => ta,
    };
})(jQuery);
