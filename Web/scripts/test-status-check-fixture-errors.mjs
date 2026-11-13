import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

// The Status Check test fixtures stand for answers the server gives. A domain result's `error` that
// is a translation key has to be one the server writes (StatusCheckService.cs), or the fixture
// describes a row no user can see and checks nothing about the rows they do see.

const serverSource = readFileSync(
  new URL(
    '../../Api/LancacheManager/Core/Services/StatusCheck/StatusCheckService.cs',
    import.meta.url
  ),
  'utf8'
);
const fixtures = ['test-status-check-added-to-list-note.mjs', 'status-check-page.mjs'];

test('the Status Check fixtures give unresolved names only error keys the server sends', () => {
  const unknown = [];
  for (const file of fixtures) {
    const source = readFileSync(new URL(file, import.meta.url), 'utf8');
    for (const [, key] of source.matchAll(/error:[^\n]*?'((?:errors|signalr)\.[\w.]+)'/g)) {
      if (!serverSource.includes(`"${key}"`)) unknown.push(`${file}: ${key}`);
    }
  }
  assert.deepEqual(
    unknown,
    [],
    `these fixture error keys are never sent by StatusCheckService.cs: ${unknown.join(', ')}`
  );
});
