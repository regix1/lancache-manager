import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const headerSource = readFileSync(
  new URL('../src/components/layout/Header.tsx', import.meta.url),
  'utf8'
);

const classListFor = (name) => {
  const match = headerSource.match(new RegExp(`<div className="${name} ([^"]+)"`));

  assert.ok(match, `Expected ${name} to declare its control-row classes`);
  return match[1];
};

test('desktop header controls retain the compact control gap at every desktop width', () => {
  const classes = classListFor('header-controls');

  assert.match(classes, /(?:^|\s)gap-2(?:\s|$)/);
  assert.doesNotMatch(classes, /(?:^|\s)(?:sm|md|lg|xl|2xl):gap-/);
});

test('phone header controls retain their tighter icon-only fit', () => {
  const classes = classListFor('header-mobile-controls');

  assert.match(classes, /(?:^|\s)gap-0\.5(?:\s|$)/);
  assert.match(classes, /(?:^|\s)xs:gap-1(?:\s|$)/);
});
