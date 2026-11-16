/* SyncedNotes – Markdown rendering (marked + DOMPurify + highlight.js). */
(function () {
    'use strict';
    const esc = SN.util.escapeHtml;

    const LANGUAGE_ALIASES = {
        cs: 'csharp', 'c#': 'csharp', razor: 'xml', cshtml: 'xml', html: 'xml', vue: 'xml', svg: 'xml',
        js: 'javascript', jsx: 'javascript', mjs: 'javascript', ts: 'typescript', tsx: 'typescript',
        py: 'python', rb: 'ruby', sh: 'bash', zsh: 'bash', shell: 'bash', console: 'bash',
        ps: 'powershell', ps1: 'powershell', pwsh: 'powershell', yml: 'yaml', md: 'markdown',
        docker: 'dockerfile', 'c++': 'cpp', kt: 'kotlin', rs: 'rust', golang: 'go', tsql: 'sql', mssql: 'sql',
        jsonc: 'json', toml: 'ini', conf: 'ini', text: 'plaintext', txt: 'plaintext', proto: 'protobuf',
    };

    /** Languages offered in the toolbar's code block menu. */
    const LANGUAGE_MENU = [
        ['csharp', 'C#'], ['javascript', 'JavaScript'], ['typescript', 'TypeScript'], ['sql', 'SQL'],
        ['json', 'JSON'], ['xml', 'HTML / XML'], ['css', 'CSS'], ['bash', 'Bash'], ['powershell', 'PowerShell'],
        ['python', 'Python'], ['java', 'Java'], ['go', 'Go'], ['rust', 'Rust'], ['cpp', 'C++'],
        ['yaml', 'YAML'], ['markdown', 'Markdown'], ['dockerfile', 'Dockerfile'], ['diff', 'Diff'], ['plaintext', 'Plain text'],
    ];

    const highlightCache = new Map();

    function resolveLanguage(lang) {
        const l = String(lang || '').trim().toLowerCase().split(/[\s{]/)[0];
        if (!l) return null;
        const name = LANGUAGE_ALIASES[l] || l;
        return window.hljs && hljs.getLanguage(name) ? name : null;
    }

    function highlight(code, language) {
        const key = `${language}\u0000${code}`;
        const cached = highlightCache.get(key);
        if (cached !== undefined) return cached;
        let html;
        try {
            html = language && language !== 'plaintext' ? hljs.highlight(code, { language, ignoreIllegals: true }).value : esc(code);
        } catch {
            html = esc(code);
        }
        if (highlightCache.size > 400) highlightCache.clear();
        highlightCache.set(key, html);
        return html;
    }

    const wikilink = {
        name: 'wikilink',
        level: 'inline',
        start(src) { const i = src.indexOf('[['); return i < 0 ? undefined : i; },
        tokenizer(src) {
            const m = /^\[\[([^\]\n|]{1,200})(?:\|([^\]\n]{1,200}))?\]\]/.exec(src);
            if (m) return { type: 'wikilink', raw: m[0], target: m[1].trim(), text: (m[2] || m[1]).trim() };
            return undefined;
        },
        renderer(token) {
            return `<a href="#" class="wikilink" data-wikilink="${esc(token.target)}">${esc(token.text)}</a>`;
        },
    };

    const md = new marked.Marked({ gfm: true, breaks: false });
    md.use({
        extensions: [wikilink],
        renderer: {
            checkbox(token) {
                return `<input type="checkbox" data-task="1"${token.checked ? ' checked' : ''} aria-label="Toggle task">`;
            },
            listitem(item) {
                if (!item.task) return false;
                return `<li class="task-list-item${item.checked ? ' done' : ''}">${this.parser.parse(item.tokens)}</li>\n`;
            },
            code(token) {
                const language = resolveLanguage(token.lang);
                const label = (token.lang || '').trim().split(/\s/)[0] || 'text';
                const body = highlight(token.text, language);
                return `<div class="code-block"><div class="code-head"><span>${esc(label)}</span>` +
                    `<button type="button" class="code-copy" data-copy-code><i class="bi bi-clipboard"></i> Copy</button></div>` +
                    `<pre><code class="hljs${language ? ' language-' + esc(language) : ''}">${body}</code></pre></div>\n`;
            },
        },
    });

    const PURIFY_CONFIG = {
        USE_PROFILES: { html: true },
        ADD_ATTR: ['target', 'data-task', 'data-wikilink', 'data-copy-code'],
        FORBID_TAGS: ['style', 'form', 'textarea', 'select', 'option', 'iframe', 'object', 'embed', 'base', 'meta', 'link'],
        FORBID_ATTR: ['style'],
        ALLOW_DATA_ATTR: false,
    };

    if (window.DOMPurify && !DOMPurify._snHooked) {
        DOMPurify._snHooked = true;
        DOMPurify.addHook('uponSanitizeElement', (node, data) => {
            // Only our own task-list checkboxes may survive as inputs.
            if (data.tagName === 'input' && (node.getAttribute('type') !== 'checkbox' || !node.hasAttribute('data-task'))) {
                node.parentNode && node.parentNode.removeChild(node);
            }
        });
        DOMPurify.addHook('afterSanitizeAttributes', (node) => {
            if (node.tagName === 'A' && node.getAttribute('href')) {
                const href = node.getAttribute('href');
                if (/^https?:\/\//i.test(href) && !href.startsWith(location.origin)) {
                    node.setAttribute('target', '_blank');
                    node.setAttribute('rel', 'noopener noreferrer');
                }
            }
            if (node.tagName === 'IMG') {
                node.setAttribute('loading', 'lazy');
                node.setAttribute('decoding', 'async');
            }
        });
    }

    function render(src) {
        if (!src) return '';
        let html;
        try {
            html = md.parse(String(src));
        } catch (e) {
            console.error(e);
            html = `<pre>${esc(src)}</pre>`;
        }
        return DOMPurify.sanitize(html, PURIFY_CONFIG);
    }

    /** Post-render touches that need the live DOM (local images, wiki links, tables). */
    function enhance(container) {
        container.querySelectorAll('img[src^="/attachments/"]').forEach((img) => {
            const id = img.getAttribute('src').split('/').pop().split('?')[0];
            const local = SN.attachments.localUrl(id);
            if (local) img.src = local;
        });
        container.querySelectorAll('a[data-wikilink]').forEach((a) => {
            const note = SN.store.findNoteByTitle(a.getAttribute('data-wikilink'));
            if (note) {
                a.setAttribute('href', `#/note/${note.id}`);
                a.title = note.title;
            } else {
                a.classList.add('missing');
                a.title = 'Create this note';
            }
        });
        container.querySelectorAll('table').forEach((t) => {
            if (t.parentElement && t.parentElement.classList.contains('table-wrap')) return;
            const wrap = document.createElement('div');
            wrap.className = 'table-wrap';
            t.parentNode.insertBefore(wrap, t);
            wrap.appendChild(t);
        });
        let i = 0;
        container.querySelectorAll('input[data-task]').forEach((box) => { box.dataset.index = String(i++); });
    }

    const TASK_LINE = /^(\s*(?:>\s*)*(?:[-*+]|\d+[.)])\s+\[)([ xX])(\].*)$/;

    /** Toggles the n-th task checkbox in the Markdown source (skipping fenced code blocks). */
    function toggleTask(src, index) {
        const lines = String(src).split('\n');
        let fence = null, n = 0;
        for (let i = 0; i < lines.length; i++) {
            const fm = /^\s*(```+|~~~+)/.exec(lines[i]);
            if (fm) {
                if (!fence) fence = fm[1][0];
                else if (fm[1][0] === fence) fence = null;
                continue;
            }
            if (fence) continue;
            const m = TASK_LINE.exec(lines[i]);
            if (!m) continue;
            if (n === index) {
                lines[i] = m[1] + (m[2] === ' ' ? 'x' : ' ') + m[3];
                return lines.join('\n');
            }
            n++;
        }
        return src;
    }

    SN.markdown = { render, enhance, toggleTask, resolveLanguage, LANGUAGE_MENU };
})();
