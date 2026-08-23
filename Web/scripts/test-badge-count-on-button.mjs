import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const badges = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), '../src/styles/components/badges.css'),
  'utf8'
);

test('neutral count chips use an opaque fill so a selected button does not paint the numeral', () => {
  const rule = badges.match(/\.themed-badge\.status-badge-neutral\.badge-count\s*\{([^}]+)\}/);
  assert.ok(rule, 'expected a compounded neutral count-chip rule');
  assert.match(rule[1], /background-color:\s*var\(--theme-bg-secondary\)/);
  assert.match(rule[1], /color:\s*var\(--theme-text-secondary\)/);
  assert.doesNotMatch(rule[1], /--theme-text-secondary-muted/);
});

test('counts inside a segmented control keep the same chip on the selected half', () => {
  const rule = badges.match(/\.segmented-control-button \.badge-count\s*\{([^}]+)\}/);
  assert.ok(rule, 'expected a segment count-chip rule');
  assert.match(rule[1], /background-color:\s*var\(--theme-bg-secondary\)/);
  assert.match(rule[1], /color:\s*var\(--theme-text-secondary\)/);
  assert.match(rule[1], /animation:\s*none/);
});
