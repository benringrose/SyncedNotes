// End-to-end tests for SyncedNotes in Chromium, using a virtual WebAuthn authenticator (real passkey ceremonies).
//
// Start a server with registration open and an empty data folder, e.g.
//   Auth__Registration=Open DataDirectory=/tmp/sn-e2e dotnet run --project src/SyncedNotes --urls http://localhost:5177
// then:
//   cd tests/e2e && npm install && npx playwright install chromium && npm test
// BASE_URL overrides the server address (default http://localhost:5177).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { chromium, request } from 'playwright';

const BASE = process.env.BASE_URL || 'http://localhost:5177';
const SHOTS = process.env.SCREENSHOT_DIR || '';
const userName = `e2e-${Date.now()}`;
let browser, context, page;
const pageErrors = [];

const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png` }); };
const noteTitles = () => page.$$eval('#noteList [data-id]', (els) => els.map((e) => e.querySelector('.ni-title, .nc-title').textContent));
const storeNotes = () => page.evaluate(() => Array.from(SN.store.state.notes.values()).filter((n) => !n.deleted));
const serverNotes = () => page.evaluate(async () => (await (await fetch('/api/sync?since=0', { headers: { 'X-SyncedNotes': '1' } })).json()).notes);
const waitForSynced = () => page.waitForFunction(() => SN.syncer.status === 'synced' && SN.syncer.pending === 0, null, { timeout: 15000 });

async function newNote(title, body) {
  await page.keyboard.press('Alt+KeyN');
  await page.waitForFunction(() => document.activeElement && document.activeElement.id === 'noteTitle');
  await page.keyboard.type(title);
  await page.keyboard.press('Enter');
  if (body) await page.keyboard.type(body);
  await page.waitForTimeout(600);
}

async function dragTo(source, target, opts = {}) {
  await source.scrollIntoViewIfNeeded();
  await target.scrollIntoViewIfNeeded();
  const a = await source.boundingBox();
  const b = await target.boundingBox();
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width / 2 + 8, a.y + a.height / 2 + 8, { steps: 4 });
  await page.mouse.move(b.x + b.width / 2, b.y + (opts.top ? 4 : b.height / 2), { steps: 20 });
  await page.waitForTimeout(250);
  await page.mouse.up();
  await page.waitForTimeout(500);
}

before(async () => {
  browser = await chromium.launch();
  context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  page = await context.newPage();
  page.on('pageerror', (e) => pageErrors.push(e.message));
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  });
});

after(async () => { await browser?.close(); });

test('creates an account with a passkey and shows recovery codes', async () => {
  await page.goto(`${BASE}/Account/Register`);
  await page.fill('#userName', userName);
  await page.fill('#displayName', 'E2E Tester');
  await page.click('#btnRegister');
  await page.waitForSelector('#recoveryStep:not(.d-none)');
  assert.equal(await page.locator('#recoveryCodeList li').count(), 10);
  await page.check('#savedCodes');
  await page.click('#btnContinue');
  await page.waitForSelector('#app:not([hidden])');
  await waitForSynced();
  assert.ok((await noteTitles()).includes('Welcome to SyncedNotes'));
  assert.deepEqual(await page.$$eval('#navBinders .label', (e) => e.map((x) => x.textContent)), ['Work', 'Home', 'Business', 'Sport']);
});

test('writes Markdown with live preview, task lists and syntax highlighting', async () => {
  await newNote('Release checklist', 'Steps for **launch day**\n');
  await page.keyboard.press('Control+Shift+Digit9');
  await page.keyboard.type('Run the tests\nTag the release\n\n');
  await page.click('#mdToolbar [title="Code block"]');
  await page.click('#codeLangMenu [data-lang="csharp"]');
  await page.keyboard.type('var ok = await deploy.RunAsync();');
  await page.waitForTimeout(500);
  const content = await page.inputValue('#noteContent');
  assert.match(content, /- \[ \] Run the tests\n- \[ \] Tag the release\n\n/);
  assert.match(content, /```csharp\nvar ok = await deploy\.RunAsync\(\);\n```/);
  assert.equal(await page.locator('#notePreview input[data-task]').count(), 2);
  assert.ok(await page.locator('#notePreview code.language-csharp .hljs-keyword').count() > 0);
  await page.click('#notePreview input[data-task][data-index="0"]');
  assert.match(await page.inputValue('#noteContent'), /- \[x\] Run the tests/);
  await shot('editor-split');
});

test('undoes and redoes typing word by word', async () => {
  await page.click('#noteContent');
  await page.keyboard.press('Control+End');
  await page.keyboard.type('\nalpha beta');
  const full = await page.inputValue('#noteContent');
  await page.keyboard.press('Control+KeyZ');
  assert.ok((await page.inputValue('#noteContent')).endsWith('alpha'));
  await page.keyboard.press('Control+KeyY');
  assert.equal(await page.inputValue('#noteContent'), full);
});

test('pins, archives and undoes note actions', async () => {
  const id = await page.evaluate(() => SN.editor.currentId());
  await page.keyboard.press('Alt+KeyP');
  await page.waitForTimeout(300);
  assert.equal((await storeNotes()).find((n) => n.id === id).pinned, true);
  await page.keyboard.press('Alt+KeyA');
  await page.waitForTimeout(300);
  let note = (await storeNotes()).find((n) => n.id === id);
  assert.equal(note.archived, true);
  assert.equal(note.pinned, false, 'archiving unpins');
  await page.keyboard.press('Escape');
  await page.click('#noteList', { position: { x: 5, y: 5 } }).catch(() => {});
  await page.keyboard.press('Control+KeyZ');
  await page.waitForTimeout(300);
  note = (await storeNotes()).find((n) => n.id === id);
  assert.equal(note.archived, false, 'Ctrl+Z restores the archived note');
});

test('organises notes with binders, tags, colours and drag & drop', async () => {
  await page.evaluate(() => SN.store.setPrefs({ sort: 'manual', viewMode: 'list' }));
  await newNote('Gym plan', 'Leg day');
  await page.fill('#tagInput', 'health');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(300);
  assert.ok(await page.locator('#navTags .tag-item:has-text("health")').count());

  const item = page.locator('#noteList .note-item:has-text("Gym plan")');
  await dragTo(item, page.locator('#navBinders a:has-text("Sport")'));
  const gym = (await storeNotes()).find((n) => n.title === 'Gym plan');
  const sport = await page.evaluate(() => SN.store.listBinders().find((b) => b.name === 'Sport').id);
  assert.equal(gym.binderId, sport, 'dropping a note on a binder moves it there');

  const first = page.locator('#grpOthers .note-item').first();
  const last = page.locator('#grpOthers .note-item').last();
  const lastTitle = await last.locator('.ni-title').textContent();
  await dragTo(last, first, { top: false });
  const order = await page.$$eval('#grpOthers .ni-title', (e) => e.map((x) => x.textContent));
  assert.ok(order.indexOf(lastTitle) <= 1, `dragged note should move up (order: ${order.join(', ')})`);

  // Opens the colour & icon dialog (don't await: it resolves when the dialog closes).
  await page.evaluate(() => { SN.commands.actions.style([SN.editor.currentId()]); });
  await page.waitForSelector('#styleModal.show');
  await page.click('#styleSwatches .swatch.c-green');
  await page.click('#styleIconGrid [data-icon="bi-trophy"]');
  await page.click('#styleSave');
  await page.waitForTimeout(400);
  const styled = (await storeNotes()).find((n) => n.title === 'Gym plan');
  assert.equal(styled.color, 'green');
  assert.equal(styled.icon, 'bi-trophy');
});

test('remembers theme and card view, and searches', async () => {
  await page.evaluate(() => SN.app.setTheme('dark'));
  await page.evaluate(() => SN.store.setPrefs({ viewMode: 'grid' }));
  await page.reload();
  await page.waitForSelector('#app:not([hidden])');
  assert.equal(await page.getAttribute('html', 'data-bs-theme'), 'dark');
  assert.ok(await page.locator('.note-card').count() > 0, 'card view is remembered');
  await page.keyboard.press('/');
  await page.keyboard.type('leg day');
  await page.waitForTimeout(400);
  assert.deepEqual(await noteTitles(), ['Gym plan']);
  await shot('cards-dark-search');
  await page.fill('#searchInput', '');
  await page.keyboard.press('Escape');
  await page.evaluate(() => { SN.app.setTheme('light'); SN.store.setPrefs({ viewMode: 'list' }); });
});

test('runs commands from the command palette', async () => {
  await page.keyboard.press('Control+KeyK');
  await page.waitForSelector('#paletteModal.show');
  await page.keyboard.type('>cards');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(400);
  assert.equal(await page.evaluate(() => SN.store.state.prefs.viewMode), 'grid');
  await page.keyboard.press('Control+KeyK');
  await page.waitForSelector('#paletteModal.show');
  await page.keyboard.type('release check');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(400);
  assert.equal(await page.inputValue('#noteTitle'), 'Release checklist');
});

test('pastes images as attachments that upload and render', async () => {
  await newNote('Diagram');
  await page.evaluate(async () => {
    const c = Object.assign(document.createElement('canvas'), { width: 64, height: 64 });
    c.getContext('2d').fillRect(0, 0, 64, 64);
    const blob = await new Promise((r) => c.toBlob(r, 'image/png'));
    const dt = new DataTransfer();
    dt.items.add(new File([blob], 'diagram.png', { type: 'image/png' }));
    document.getElementById('noteContent').dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
  });
  await page.waitForTimeout(600);
  const md = await page.inputValue('#noteContent');
  const url = md.match(/\/attachments\/[0-9a-f-]{36}/)[0];
  await waitForSynced();
  const res = await page.evaluate(async (u) => { const r = await fetch(u); return [r.status, r.headers.get('content-type')]; }, url);
  assert.deepEqual(res, [200, 'image/png']);
  assert.equal(await page.locator('#notePreview img').count(), 1);
});

test('app lock is enforced by the server and unlocks with PIN or passkey', async () => {
  await page.evaluate(() => SN.ui.api('PUT', '/api/account/app-lock', { enabled: true, timeoutMinutes: 5, lockOnLaunch: false }));
  await page.evaluate(() => SN.ui.api('PUT', '/api/account/app-lock/pin', { pin: '482915' }));
  await page.evaluate(() => SN.app.refreshSession());
  await page.keyboard.press('Control+Shift+KeyL');
  await page.waitForSelector('#lockScreen:not([hidden])');
  const status = await page.evaluate(async () => (await fetch('/api/sync?since=0', { headers: { 'X-SyncedNotes': '1' } })).status);
  assert.equal(status, 423, 'notes API is locked');
  await page.fill('#pinInput', '000000');
  await page.click('#btnUnlockPin');
  await page.waitForFunction(() => !document.getElementById('lockError').hidden);
  await page.fill('#pinInput', '482915');
  await page.click('#btnUnlockPin');
  await page.waitForSelector('#lockScreen', { state: 'hidden' });
  await page.keyboard.press('Control+Shift+KeyL');
  await page.waitForSelector('#lockScreen:not([hidden])');
  await page.click('#btnUnlockPasskey');
  await page.waitForSelector('#lockScreen', { state: 'hidden' });
  assert.equal(await page.evaluate(async () => (await fetch('/api/sync?since=0', { headers: { 'X-SyncedNotes': '1' } })).status), 200);
  await page.evaluate(() => SN.ui.api('PUT', '/api/account/app-lock', { enabled: false, timeoutMinutes: 5, lockOnLaunch: false }));
});

test('works offline after a reload and syncs when back online', async () => {
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.reload(); // make sure the page is controlled by the service worker
  await page.waitForSelector('#app:not([hidden])');
  await context.setOffline(true);
  await page.reload();
  await page.waitForSelector('#app:not([hidden])');
  assert.ok((await noteTitles()).length > 0, 'notes are available offline');
  await newNote('Offline thought', 'written without a connection');
  assert.match(await page.textContent('#syncStatus'), /Offline/);
  await context.setOffline(false);
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await waitForSynced();
  assert.ok((await serverNotes()).some((n) => n.title === 'Offline thought'));
});

test('quick-add links save instantly for the user but ask when opened from another site', async () => {
  await page.goto(`${BASE}/new?text=${encodeURIComponent('Buy milk')}`);
  await page.waitForSelector('#app:not([hidden])');
  await page.waitForFunction(() => document.getElementById('noteTitle').value === 'Buy milk');
  const crossSite = new URL(BASE);
  crossSite.hostname = crossSite.hostname === 'localhost' ? '127.0.0.1' : 'localhost';
  await page.goto(`${crossSite.origin}/healthz`);
  await page.evaluate((u) => { location.href = u; }, `${BASE}/new?text=${encodeURIComponent('Sneaky note')}`);
  await page.waitForSelector('#confirmModal.show');
  await page.click('#confirmCancel');
  await page.waitForTimeout(300);
  assert.ok(!(await storeNotes()).some((n) => n.title === 'Sneaky note'));
});

test('API tokens power the quick API used by the PowerToys extension', async () => {
  const { token } = await page.evaluate(() => SN.ui.api('POST', '/api/account/tokens', { name: 'e2e' }));
  const api = await request.newContext({ baseURL: BASE, extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
  const created = await api.post('/api/v1/quick/notes', { data: { content: 'From PowerToys\nSecond line', binder: 'work', tags: ['inbox'] } });
  assert.equal(created.status(), 201);
  const body = await created.json();
  assert.equal(body.title, 'From PowerToys');
  assert.match(body.url, /\/#\/note\/[0-9a-f-]{36}$/);
  const found = await (await api.get('/api/v1/quick/notes?q=powertoys')).json();
  assert.deepEqual(found.map((n) => [n.title, n.binder]), [['From PowerToys', 'Work']]);
  const anon = await request.newContext({ baseURL: BASE, extraHTTPHeaders: { Authorization: 'Bearer snp_invalid' } });
  assert.equal((await anon.get('/api/v1/quick/notes')).status(), 401);
});

test('no uncaught page errors', () => {
  assert.deepEqual(pageErrors, []);
});
