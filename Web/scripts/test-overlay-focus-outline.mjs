import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const reset = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), '../src/styles/base/reset.css'),
  'utf8'
);

test('keyboard controls still get the global focus-visible ring', () => {
  assert.match(
    reset,
    /\*:focus-visible\s*\{[^}]*outline:\s*2px solid var\(--theme-border-focus\)/s
  );
});

test('trap shells drop the ring so a modal does not halo when inner content blurs', () => {
  assert.match(reset, /\[role='dialog'\]:focus-visible/);
  assert.match(reset, /\[role='alertdialog'\]:focus-visible/);
  assert.doesNotMatch(reset, /\[tabindex='-1'\]:focus-visible/);

  const trapBlock = reset.slice(reset.indexOf("[role='dialog']:focus"));
  assert.match(trapBlock, /outline:\s*none/);
  assert.match(trapBlock, /outline-offset:\s*0/);
});
