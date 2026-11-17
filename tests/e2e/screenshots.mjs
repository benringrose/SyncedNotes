// Captures the README and PWA install screenshots from a demo account.
// Needs a server with registration open and an empty data folder (see app.test.mjs), then:
//   node screenshots.mjs
// Writes screenshots/*.jpg and SyncedNotes/wwwroot/icons/screenshots/*.png.
import { chromium, devices } from 'playwright';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const BASE = process.env.BASE_URL || 'http://localhost:5177';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const readmeShots = resolve(root, 'screenshots');
const pwa = resolve(root, 'SyncedNotes/wwwroot/icons/screenshots');
mkdirSync(readmeShots, { recursive: true });
mkdirSync(pwa, { recursive: true });

const DEMO = [
  {
    title: 'Q4 product roadmap', binder: 'Work', color: 'blue', icon: 'bi-rocket-takeoff', tags: ['planning', 'q4'], pinned: true,
    content: '## Goals\n\n- [x] Ship offline sync\n- [x] Passkey sign-in\n- [ ] Shared binders\n- [ ] Calendar integration\n\n## Milestones\n\n| Milestone | Owner | Due |\n| --- | --- | --- |\n| Beta | Sam | 14 Oct |\n| Launch | Priya | 2 Dec |\n\n> Keep scope small and ship weekly.',
  },
  {
    title: 'API rate limiting', binder: 'Work', color: 'indigo', icon: 'bi-code-slash', tags: ['dotnet', 'api'],
    content: 'Fixed-window limiter per client IP for the auth endpoints:\n\n```csharp\nbuilder.Services.AddRateLimiter(options =>\n{\n    options.AddPolicy("auth", context =>\n        RateLimitPartition.GetFixedWindowLimiter(\n            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",\n            _ => new FixedWindowRateLimiterOptions\n            {\n                PermitLimit = 40,\n                Window = TimeSpan.FromMinutes(1),\n            }));\n});\n```\n\nCheck the busiest clients:\n\n```sql\nSELECT TOP 10 ClientIp, COUNT(*) AS Requests\nFROM RequestLog\nWHERE Status = 429\nGROUP BY ClientIp\nORDER BY Requests DESC;\n```\n\n- [ ] Add `Retry-After` header\n- [x] Return **429** with problem details',
  },
  {
    title: 'Weekly meal plan', binder: 'Home', color: 'green', icon: '🍳', tags: ['food'],
    content: '| Day | Dinner |\n| --- | --- |\n| Mon | Lentil curry |\n| Tue | Salmon & greens |\n| Wed | Homemade pizza |\n| Thu | Stir-fry |\n| Fri | Tacos |\n\n**Shopping:** rice, limes, coriander, tortillas.',
  },
  {
    title: 'Marathon training', binder: 'Sport', color: 'orange', icon: 'bi-trophy', tags: ['running', 'health'],
    content: '### Week 6\n\n1. Easy run – 8 km\n2. Intervals – 6 × 800 m\n3. Rest\n4. Tempo – 10 km\n5. Long run – **24 km**\n\nRemember: hydrate and sleep 8 hours.',
  },
  {
    title: 'Invoice follow-ups', binder: 'Business', color: 'purple', icon: 'bi-cash-coin', tags: ['finance'],
    content: '- [ ] Acme Ltd – #1042 (30 days)\n- [ ] Northwind – #1047\n- [x] Contoso – paid\n\nSend reminders on Friday.',
  },
  {
    title: 'Reading list', binder: 'Home', color: 'pink', icon: '📚', tags: ['reading'],
    content: '- *The Pragmatic Programmer*\n- *Designing Data-Intensive Applications*\n- *Atomic Habits*',
  },
  {
    title: 'App ideas', binder: null, color: 'yellow', icon: 'bi-lightbulb', tags: ['ideas'],
    content: '- Markdown slides from a note\n- Voice memos that become notes\n- Share a binder with family',
  },
];

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
const page = await context.newPage();
const cdp = await context.newCDPSession(page);
await cdp.send('WebAuthn.enable');
const { authenticatorId } = await cdp.send('WebAuthn.addVirtualAuthenticator', {
  options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
});

await page.goto(`${BASE}/Account/Register`);
await page.fill('#userName', `alex-${Date.now()}`);
await page.fill('#displayName', 'Alex Morgan');
await page.click('#btnRegister');
await page.waitForSelector('#recoveryStep:not(.d-none)');
await page.check('#savedCodes');
await page.click('#btnContinue');
await page.waitForSelector('#app:not([hidden])');
await page.waitForFunction(() => SN.store.listBinders().length === 4);

await page.evaluate(async (notes) => {
  const binders = Object.fromEntries(SN.store.listBinders().map((b) => [b.name, b.id]));
  let t = Date.now() - notes.length * 3600_000;
  for (const n of notes.slice().reverse()) {
    t += 3600_000;
    await SN.store.createNote({ ...n, binderId: n.binder ? binders[n.binder] : null, createdAt: t - 86400_000, updatedAt: t });
  }
  await SN.store.setPrefs({ sort: 'updated', sortDir: 'desc', viewMode: 'list', editorMode: 'split' });
  await SN.syncer.now();
}, DEMO);

const open = async (title) => {
  await page.evaluate((t) => SN.app.openNote(SN.store.findNoteByTitle(t).id), title);
  await page.waitForTimeout(600);
};

// Desktop, light, list + split editor.
await page.evaluate(() => window.SNTheme.set('light'));
await open('API rate limiting');
await page.mouse.move(0, 0);
await page.screenshot({ path: `${readmeShots}/desktop-light.jpg`, type: 'jpeg', quality: 88 });

// Desktop, dark, cards.
await page.evaluate(() => { window.SNTheme.set('dark'); SN.app.closeNote(); return SN.store.setPrefs({ viewMode: 'grid' }); });
await page.waitForTimeout(600);
await page.screenshot({ path: `${readmeShots}/desktop-dark-cards.jpg`, type: 'jpeg', quality: 88 });

// Command palette.
await page.evaluate(() => SN.store.setPrefs({ viewMode: 'list' }));
await page.keyboard.press('Control+KeyK');
await page.keyboard.type('mar');
await page.waitForTimeout(400);
await page.screenshot({ path: `${readmeShots}/command-palette.jpg`, type: 'jpeg', quality: 88 });
await page.keyboard.press('Escape');

// PWA install screenshots (1x, exact sizes declared in the manifest).
await page.evaluate(() => window.SNTheme.set('light'));
await page.setViewportSize({ width: 1280, height: 800 });
await open('Q4 product roadmap');
const wide = await context.newPage();
await wide.setViewportSize({ width: 1280, height: 800 });
await page.screenshot({ path: `${pwa}/wide.png`, scale: 'css' });
await wide.close();

// Phone.
const state = await context.storageState();
const credentials = (await cdp.send('WebAuthn.getCredentials', { authenticatorId })).credentials;
const phone = await browser.newContext({ ...devices['Pixel 7'], storageState: state });
const p = await phone.newPage();
await p.goto(`${BASE}/`);
await p.waitForSelector('#app:not([hidden])');
await p.waitForTimeout(1200);
await p.screenshot({ path: `${readmeShots}/phone-list.jpg`, type: 'jpeg', quality: 88 });
await p.screenshot({ path: `${pwa}/narrow.png`, scale: 'css' });
await p.evaluate((t) => SN.app.openNote(SN.store.findNoteByTitle(t).id), 'Q4 product roadmap');
await p.waitForTimeout(500);
await p.evaluate(() => SN.editor.setMode('preview'));
await p.waitForTimeout(400);
await p.screenshot({ path: `${readmeShots}/phone-note.jpg`, type: 'jpeg', quality: 88 });

console.log(`Screenshots written (${credentials.length} passkey).`);
await browser.close();
