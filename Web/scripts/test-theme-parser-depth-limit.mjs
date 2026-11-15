import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import test from 'node:test';

/**
 * Theme files are uploaded by any account holder and parsed in every browser that loads the theme
 * list (`src/services/theme.service.ts`, `parseTomlTheme`). The parser is recursive, so a file nested
 * thousands of levels deep must be refused as a bad file, with the line it failed on, rather than
 * overflowing the call stack. This runs the parser the app bundles.
 */

const TOML = createRequire(import.meta.url)('toml');

test('a theme file nested past the parser depth limit is refused as a parse error', () => {
  const depth = 5000;
  const theme = [
    '[meta]',
    'id = "nested"',
    'name = "Nested"',
    '',
    '[colors]',
    `deep = ${'['.repeat(depth)}${']'.repeat(depth)}`,
    ''
  ].join('\n');

  let failure = null;
  try {
    TOML.parse(theme);
  } catch (error) {
    failure = error;
  }
  assert.notEqual(failure, null, 'a theme nested 5000 levels deep was accepted');
  assert.notEqual(
    failure.name,
    'RangeError',
    `the parser overflowed the call stack instead of refusing the file: ${failure.message}`
  );
  assert.equal(
    typeof failure.line,
    'number',
    `the refusal names no line: ${failure.name}: ${failure.message}`
  );
});
