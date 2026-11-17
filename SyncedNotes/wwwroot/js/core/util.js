/* SyncedNotes – shared helpers. Works in pages and in the service worker (no DOM access). */
(function (root) {
    'use strict';
    const SN = (root.SN = root.SN || {});

    const util = {
        uuid() {
            if (root.crypto && typeof root.crypto.randomUUID === 'function') return root.crypto.randomUUID();
            const b = new Uint8Array(16);
            root.crypto.getRandomValues(b);
            b[6] = (b[6] & 0x0f) | 0x40;
            b[8] = (b[8] & 0x3f) | 0x80;
            const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
            return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
        },

        sleep: (ms) => new Promise((r) => setTimeout(r, ms)),

        /** Debounce with an optional maxWait so continuous typing still flushes regularly. */
        debounce(fn, wait, maxWait) {
            let timer = null, first = 0, lastArgs = null;
            const run = () => {
                timer = null; first = 0;
                const args = lastArgs; lastArgs = null;
                fn.apply(null, args);
            };
            const debounced = (...args) => {
                lastArgs = args;
                const now = Date.now();
                if (!first) first = now;
                clearTimeout(timer);
                const remaining = maxWait ? Math.max(0, Math.min(wait, maxWait - (now - first))) : wait;
                timer = setTimeout(run, remaining);
            };
            debounced.flush = () => { if (timer) { clearTimeout(timer); run(); } };
            debounced.cancel = () => { clearTimeout(timer); timer = null; first = 0; lastArgs = null; };
            debounced.pending = () => timer !== null;
            return debounced;
        },

        escapeHtml(value) {
            return String(value ?? '').replace(/[&<>"']/g, (c) =>
                ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
        },

        clamp: (v, min, max) => Math.min(max, Math.max(min, v)),

        /** Plain-text version of Markdown for snippets and search. */
        stripMarkdown(md) {
            if (!md) return '';
            return String(md)
                .replace(/```[\s\S]*?(```|$)/g, (m) => ' ' + m.replace(/```[^\n]*\n?/g, ' ') + ' ')
                .replace(/!\[([^\]]*)\]\([^)]*\)/g, ' ')
                .replace(/\[\[([^\]|]+)(\|([^\]]+))?\]\]/g, (m, a, b, c) => c || a)
                .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
                .replace(/<[^>]+>/g, ' ')
                .replace(/^\s{0,3}(#{1,6}|>+|[-*+]\s+\[[ xX]\]|[-*+]|\d+[.)])\s+/gm, '')
                .replace(/(\*\*|__|~~|`|\*|_)/g, '')
                .replace(/^\s*\|?(\s*:?-{3,}:?\s*\|)+\s*$/gm, ' ')
                .replace(/\|/g, ' ')
                .replace(/\s+/g, ' ')
                .trim();
        },

        wordCount(text) {
            const m = String(text || '').match(/[\p{L}\p{N}'’-]+/gu);
            return m ? m.length : 0;
        },

        normalizeTag(raw) {
            return String(raw || '').trim().replace(/^#+/, '').replace(/[\s,]+/g, ' ').replace(/\s/g, '-').slice(0, 50);
        },

        /** Simple fuzzy matcher. Returns a score (higher is better) or -1 when not matched. */
        fuzzyScore(query, text) {
            query = String(query || '').toLowerCase().trim();
            text = String(text || '').toLowerCase();
            if (!query) return 0;
            const direct = text.indexOf(query);
            if (direct >= 0) return 1000 - direct * 2 + (direct === 0 || /\W/.test(text[direct - 1]) ? 200 : 0) - text.length * 0.1;
            let score = 0, ti = 0, streak = 0;
            for (let qi = 0; qi < query.length; qi++) {
                const ch = query[qi];
                if (ch === ' ') continue;
                const found = text.indexOf(ch, ti);
                if (found < 0) return -1;
                streak = found === ti ? streak + 1 : 0;
                score += 10 + streak * 5 + (found === 0 || /\W/.test(text[found - 1]) ? 15 : 0) - Math.min(found - ti, 10);
                ti = found + 1;
            }
            return score - text.length * 0.1;
        },

        /** "5 minutes ago", or with short=true a compact "5m" / "3h" / "2d" for tight spaces. */
        formatRelative(ms, short) {
            if (!ms) return '';
            const diff = Date.now() - ms;
            const abs = Math.abs(diff);
            if (short) {
                if (abs < 60e3) return 'now';
                if (abs < 3600e3) return `${Math.round(abs / 60e3)}m`;
                if (abs < 86400e3) return `${Math.round(abs / 3600e3)}h`;
                if (abs < 7 * 86400e3) return `${Math.round(abs / 86400e3)}d`;
            }
            if (abs < 45 * 1000) return 'just now';
            const rtf = util._rtf || (util._rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' }));
            const units = [['minute', 60e3, 60], ['hour', 3600e3, 24], ['day', 86400e3, 7]];
            for (const [unit, size, limit] of units) {
                const v = Math.round(abs / size);
                if (v < limit) return rtf.format(diff > 0 ? -v : v, unit);
            }
            const d = new Date(ms);
            const sameYear = d.getFullYear() === new Date().getFullYear();
            return d.toLocaleDateString(undefined, sameYear ? { month: 'short', day: 'numeric' } : { year: 'numeric', month: 'short', day: 'numeric' });
        },

        formatDateTime(ms) {
            if (!ms) return '';
            return new Date(ms).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
        },

        formatBytes(bytes) {
            if (!bytes && bytes !== 0) return '';
            const u = ['B', 'KB', 'MB', 'GB'];
            let i = 0, v = bytes;
            while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
            return `${v < 10 && i ? v.toFixed(1) : Math.round(v)} ${u[i]}`;
        },

        safeFileName(name, fallback) {
            const clean = String(name || '').replace(/[\\/:*?"<>|\u0000-\u001f]/g, '-').replace(/\s+/g, ' ').trim().replace(/^\.+|\.+$/g, '');
            return (clean || fallback || 'Untitled').slice(0, 80);
        },

        /** Parses YAML-ish front matter (the subset written by SyncedNotes exports). */
        parseFrontMatter(text) {
            const src = String(text || '').replace(/^﻿/, '').replace(/\r\n?/g, '\n');
            const m = /^---\n([\s\S]*?)\n---\n?/.exec(src);
            if (!m) return { meta: {}, body: src };
            const meta = {};
            for (const line of m[1].split('\n')) {
                const kv = /^([A-Za-z][\w-]*):\s*(.*)$/.exec(line);
                if (!kv) continue;
                meta[kv[1].toLowerCase()] = util._parseYamlValue(kv[2].trim());
            }
            return { meta, body: src.slice(m[0].length).replace(/^\n/, '') };
        },

        _parseYamlValue(v) {
            if (v === '') return '';
            if (v === 'true') return true;
            if (v === 'false') return false;
            if (/^-?\d+(\.\d+)?$/.test(v)) return Number(v);
            if (v.startsWith('[') && v.endsWith(']')) {
                try { return JSON.parse(v); } catch { /* fall through to a loose parse */ }
                return v.slice(1, -1).split(',').map((s) => s.trim().replace(/^["']|["']$/g, '')).filter(Boolean);
            }
            if (v.startsWith('"')) { try { return JSON.parse(v); } catch { return v.slice(1, -1); } }
            if (v.startsWith("'") && v.endsWith("'")) return v.slice(1, -1).replace(/''/g, "'");
            return v;
        },

        buildFrontMatter(note, binderName) {
            const q = (s) => JSON.stringify(String(s ?? ''));
            const lines = ['---', `title: ${q(note.title)}`];
            if (binderName) lines.push(`binder: ${q(binderName)}`);
            if (note.tags && note.tags.length) lines.push(`tags: [${note.tags.map(q).join(', ')}]`);
            if (note.pinned) lines.push('pinned: true');
            if (note.archived) lines.push('archived: true');
            if (note.color) lines.push(`color: ${note.color}`);
            if (note.icon) lines.push(`icon: ${q(note.icon)}`);
            lines.push(`created: ${new Date(note.createdAt || Date.now()).toISOString()}`);
            lines.push(`updated: ${new Date(note.updatedAt || Date.now()).toISOString()}`);
            lines.push(`id: ${note.id}`, '---', '', '');
            return lines.join('\n');
        },

        deriveTitle(content) {
            // The title comes from the first meaningful line, so the start of the note is enough.
            for (const raw of String(content || '').slice(0, 4000).split('\n')) {
                const line = raw.trim().replace(/^(#{1,6}\s+|>\s*|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)+/, '').trim();
                if (!line || line.startsWith('```')) continue;
                return line.length > 80 ? line.slice(0, 80).trimEnd() + '…' : line;
            }
            return '';
        },
    };

    SN.util = util;
})(typeof self !== 'undefined' ? self : this);
