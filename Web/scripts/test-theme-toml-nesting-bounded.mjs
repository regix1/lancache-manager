import assert from 'node:assert/strict';
import test from 'node:test';
import * as TOML from 'toml';

/**
 * Every theme file a user uploads is read by the TOML parser in the browser
 * (`Web/src/services/theme.service.ts:4`, `:839`). A file whose arrays or inline tables nest
 * thousands of levels deep must be refused as a malformed theme, with the parser's own error,
 * rather than recursing until the page's call stack runs out. The parser the app ships is the one
 * imported here, the same way the theme service imports it.
 */

const DEPTH = 5000;
const NESTED = {
  arrays: `colors = ${'['.repeat(DEPTH)}${']'.repeat(DEPTH)}\n`,
  'inline tables': `colors = ${'{ a = '.repeat(DEPTH)}1${' }'.repeat(DEPTH)}\n`
};

test('a theme file nested thousands of levels deep is refused without exhausting the stack', () => {
  const wrong = [];
  for (const [shape, text] of Object.entries(NESTED)) {
    let outcome = 'parsed';
    try {
      TOML.parse(text);
    } catch (error) {
      outcome = error instanceof RangeError ? `stack overflow (${error.message})` : 'refused';
    }
    if (outcome !== 'refused') wrong.push(`${DEPTH} nested ${shape}: ${outcome}`);
  }
  assert.deepEqual(
    wrong,
    [],
    `the theme parser recurses without a bound on a deeply nested theme file: ${wrong.join('; ')}`
  );
});
