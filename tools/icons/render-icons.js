// Renders the app icons (PNG + ICO) from the SVG sources with Playwright/Chromium.
// Usage: node tools/icons/render-icons.js   (needs the `playwright` package)
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'SyncedNotes/wwwroot/icons');
const iconSvg = fs.readFileSync(path.join(out, 'icon.svg'), 'utf8');
const maskableSvg = fs.readFileSync(path.join(__dirname, 'maskable.svg'), 'utf8');

const glyph = (body) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96" width="96" height="96">
  <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#6366f1"/><stop offset="1" stop-color="#a855f7"/></linearGradient></defs>
  <rect width="96" height="96" rx="24" fill="url(#g)"/>${body}</svg>`;
const shortcutNew = glyph('<path d="M48 26v44M26 48h44" stroke="#fff" stroke-width="9" stroke-linecap="round"/>');
const shortcutSearch = glyph('<circle cx="43" cy="43" r="16" fill="none" stroke="#fff" stroke-width="8"/><path d="M55 55l14 14" stroke="#fff" stroke-width="9" stroke-linecap="round"/>');

async function render(page, svg, size, file, transparent) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace(/width="\d+" height="\d+"/, `width="${size}" height="${size}"`)}</body></html>`);
  const buf = await page.screenshot({ omitBackground: transparent, clip: { x: 0, y: 0, width: size, height: size } });
  if (file) fs.writeFileSync(path.join(out, file), buf);
  return buf;
}

function ico(pngs) {
  // ICO container with PNG-compressed entries (supported by all current browsers and Windows).
  const header = Buffer.alloc(6 + 16 * pngs.length);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(pngs.length, 4);
  let offset = header.length;
  pngs.forEach(({ size, data }, i) => {
    const e = 6 + i * 16;
    header.writeUInt8(size >= 256 ? 0 : size, e); header.writeUInt8(size >= 256 ? 0 : size, e + 1);
    header.writeUInt8(0, e + 2); header.writeUInt8(0, e + 3);
    header.writeUInt16LE(1, e + 4); header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(data.length, e + 8); header.writeUInt32LE(offset, e + 12);
    offset += data.length;
  });
  return Buffer.concat([header, ...pngs.map((p) => p.data)]);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const s of [192, 512]) await render(page, iconSvg, s, `icon-${s}.png`, true);
  for (const s of [192, 512]) await render(page, maskableSvg, s, `maskable-${s}.png`, false);
  await render(page, maskableSvg, 180, 'apple-touch-icon.png', false);
  await render(page, shortcutNew, 96, 'shortcut-new.png', true);
  await render(page, shortcutSearch, 96, 'shortcut-search.png', true);
  const favs = [];
  for (const s of [16, 32, 48]) favs.push({ size: s, data: await render(page, iconSvg, s, null, true) });
  fs.writeFileSync(path.join(root, 'SyncedNotes/wwwroot/favicon.ico'), ico(favs));
  await browser.close();
  console.log('Icons written to', out);
})();
