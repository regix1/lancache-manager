import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const __dirname = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = resolve(__dirname, '..');
const stripCss = readFileSync(
  resolve(WEB_ROOT, 'src/components/common/CondensedNotificationStrip.css'),
  'utf8'
);
const stripComponent = readFileSync(
  resolve(WEB_ROOT, 'src/components/common/CondensedNotificationStrip.tsx'),
  'utf8'
);
const barComponent = readFileSync(
  resolve(WEB_ROOT, 'src/components/common/UniversalNotificationBar.tsx'),
  'utf8'
);

test('pointer hover does not start a glow that opening immediately reverses', () => {
  assert.doesNotMatch(
    stripCss,
    /\.condensed-strip\s*:\s*hover[\s\S]*?\.condensed-strip-glow\s*\{[\s\S]*?opacity\s*:\s*1/
  );
  assert.match(
    stripCss,
    /\.condensed-strip-line\s*:\s*focus-visible\s+\.condensed-strip-glow\s*\{[\s\S]*?opacity\s*:\s*1/
  );
});

test('the revealed panel reports its parent edge before paint', () => {
  assert.match(
    stripComponent,
    /useLayoutEffect\(\(\)\s*=>\s*\{\s*onOpenChangeRef\.current\?\.\(panelVisible\);\s*\},\s*\[panelVisible\]\);/
  );
});

test('the notification surface keeps its border slot and limits transitions', () => {
  assert.match(barComponent, /\bborder-b\b/);
  assert.match(barComponent, /\bborder-transparent\b/);
  assert.match(barComponent, /transition-\[transform,opacity\]/);
  assert.doesNotMatch(barComponent, /bg-\[var\(--theme-nav-bg\)\]\s+transition\s+duration-300/);
});
