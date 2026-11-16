/* SyncedNotes – undo/redo history for the Markdown editor.
 * Consecutive typing (or deleting) is grouped into one step; pauses, new lines, paste, formatting
 * commands and cursor jumps start a new step. Each step remembers the selection before and after it,
 * so undo/redo put the cursor back where the change happened. */
(function () {
    'use strict';

    const MAX_ENTRIES = 300;
    const MAX_TOTAL_CHARS = 8_000_000;
    const GROUP_MS = 1000;

    const sameSel = (a, b) => a && b && a.start === b.start && a.end === b.end;

    class TextHistory {
        constructor(value, sel) {
            sel = sel || { start: 0, end: 0 };
            this.entries = [{ value: value || '', selBefore: sel, selAfter: sel }];
            this.index = 0;
            this.lastKind = null;
            this.lastTime = 0;
        }

        get current() { return this.entries[this.index]; }
        canUndo() { return this.index > 0; }
        canRedo() { return this.index < this.entries.length - 1; }
        breakGroup() { this.lastKind = null; }

        /**
         * Records a new state. kind is 'insert' | 'delete' (mergeable) or anything else (always a new step).
         */
        record(value, selBefore, selAfter, kind) {
            const top = this.entries[this.index];
            if (top.value === value) {
                top.selAfter = selAfter;
                return;
            }
            const now = Date.now();
            const mergeable = (kind === 'insert' || kind === 'delete') &&
                kind === this.lastKind &&
                now - this.lastTime < GROUP_MS &&
                this.index > 0 &&
                this.index === this.entries.length - 1 &&
                sameSel(top.selAfter, selBefore);

            if (mergeable) {
                top.value = value;
                top.selAfter = selAfter;
            } else {
                this.entries.splice(this.index + 1);
                this.entries.push({ value, selBefore, selAfter });
                this.index = this.entries.length - 1;
                this.trim();
            }
            this.lastKind = kind;
            this.lastTime = now;
        }

        undo() {
            if (!this.canUndo()) return null;
            const undone = this.entries[this.index];
            this.index--;
            this.lastKind = null;
            return { value: this.entries[this.index].value, sel: undone.selBefore };
        }

        redo() {
            if (!this.canRedo()) return null;
            this.index++;
            this.lastKind = null;
            const entry = this.entries[this.index];
            return { value: entry.value, sel: entry.selAfter };
        }

        trim() {
            let total = this.entries.reduce((sum, e) => sum + e.value.length, 0);
            while (this.entries.length > 2 && (this.entries.length > MAX_ENTRIES || total > MAX_TOTAL_CHARS)) {
                total -= this.entries[0].value.length;
                this.entries.shift();
                this.index--;
            }
        }
    }

    SN.TextHistory = TextHistory;
})();
