/**
 * The i18n key validator caps how many candidate keys it will follow out of one
 * expression, and how far it will follow a chain of constants. A cap that drops
 * candidates quietly is worse than no check at all: the run prints PASS for keys
 * it never looked up.
 *
 * These checks build a source tree that sits above every cap, out of keys that
 * all resolve in both locales. Silent truncation therefore ends in a clean PASS,
 * which is exactly the state being guarded against; the validator has to fail
 * instead and say which cap it hit and where. The second check runs the same
 * shapes below the cap, so the first check can only be failing because of the
 * caps and not because a fixture key is missing. [15]
 *
 * Point I18N_VALIDATOR at another copy of the script to check that copy instead.
 */
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const VALIDATOR = process.env.I18N_VALIDATOR
  ? resolve(process.env.I18N_VALIDATOR)
  : fileURLToPath(new URL('./validate-i18n-keys.mjs', import.meta.url));

const KEY_SHAPE = /^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$/;

/** Every cause the validator can report, so a cap cannot be added without one. */
const CAUSES = [
  'ternary',
  'fallback',
  'concatenation',
  'template',
  'lookup table',
  'helper call',
  'chain depth'
];

const locales = await Promise.all(
  ['en', 'zh'].map(async (locale) => {
    const contents = await readFile(
      new URL(`../src/i18n/locales/${locale}.json`, import.meta.url),
      'utf8'
    );
    return JSON.parse(contents);
  })
);

function lookup(tree, key) {
  return key.split('.').reduce((node, part) => {
    if (node === undefined || node === null || typeof node !== 'object') return undefined;
    return node[part];
  }, tree);
}

/** A key that resolves in every locale, so no fixture can fail on a missing key. */
function sharedKey(tree, path = []) {
  for (const [name, value] of Object.entries(tree)) {
    const next = [...path, name];
    const key = next.join('.');
    if (typeof value === 'string') {
      if (!KEY_SHAPE.test(key)) continue;
      if (locales.every((locale) => typeof lookup(locale, key) === 'string')) return key;
      continue;
    }
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      const found = sharedKey(value, next);
      if (found) return found;
    }
  }
  return undefined;
}

const KEY = sharedKey(locales[0]);
assert.ok(KEY, 'the locales share no key, so there is nothing to build a fixture from');

const HEAD = KEY.slice(0, KEY.indexOf('.'));
const TAIL = KEY.slice(KEY.indexOf('.'));

const table = (count, value) =>
  Array.from({ length: count }, (_, index) => `  a${index}: '${value}'`).join(',\n');

const chainOf = (length) =>
  Array.from({ length }, (_, index) =>
    index === 0 ? `const k0 = '${KEY}';` : `const k${index} = k${index - 1};`
  ).join('\n');

const returnsOf = (count) =>
  Array.from({ length: count - 1 }, (_, index) => `  if (n === ${index}) return '${KEY}';`).join(
    '\n'
  );

/**
 * One file per shape the validator caps. `width` is how many candidates a single
 * lookup table holds; `arm` keeps each half of a two-sided shape under the cap so
 * only the combined list crosses it.
 */
function writeFixture(dir, width, chain) {
  const arm = Math.ceil(width / 2);

  writeFileSync(
    join(dir, 'lookup-table.ts'),
    `const KEYS: Record<string, string> = {\n${table(width, KEY)}\n};\n\n` +
      `export function tableLabel(t: (key: string) => string, id: string): string {\n` +
      `  return t(KEYS[id]);\n}\n`
  );

  writeFileSync(
    join(dir, 'helper-call.ts'),
    `function pickKey(n: number): string {\n${returnsOf(width)}\n  return '${KEY}';\n}\n\n` +
      `export function helperLabel(t: (key: string) => string, n: number): string {\n` +
      `  return t(pickKey(n));\n}\n`
  );

  const arms =
    `const LEFT: Record<string, string> = {\n${table(arm, KEY)}\n};\n\n` +
    `const RIGHT: Record<string, string> = {\n${table(arm, KEY)}\n};\n\n`;

  writeFileSync(
    join(dir, 'ternary.ts'),
    `${arms}export function branchLabel(t: (key: string) => string, flag: boolean, id: string): string {\n` +
      `  return t(flag ? LEFT[id] : RIGHT[id]);\n}\n`
  );

  writeFileSync(
    join(dir, 'fallback.ts'),
    `${arms}export function eitherLabel(t: (key: string) => string, id: string): string {\n` +
      `  return t(LEFT[id] ?? RIGHT[id]);\n}\n`
  );

  writeFileSync(
    join(dir, 'concatenation.ts'),
    `const TAILS: Record<string, string> = {\n${table(arm, TAIL)}\n};\n\n` +
      `export function joinedLabel(t: (key: string) => string, flag: boolean, id: string): string {\n` +
      `  return t((flag ? '${HEAD}' : '${HEAD}') + TAILS[id]);\n}\n`
  );

  writeFileSync(
    join(dir, 'template.ts'),
    `const HEADS: Record<string, string> = {\n${table(arm, HEAD)}\n};\n\n` +
      `const TAILS: Record<string, string> = {\n${table(2, TAIL)}\n};\n\n` +
      `export function templateLabel(t: (key: string) => string, a: string, b: string): string {\n` +
      '  return t(`${HEADS[a]}${TAILS[b]}`);\n}\n'
  );

  writeFileSync(
    join(dir, 'chain-depth.ts'),
    `${chainOf(chain)}\n\nexport function chainLabel(t: (key: string) => string): string {\n` +
      `  return t(k${chain - 1});\n}\n`
  );
}

function runOn(width, chain) {
  const dir = mkdtempSync(join(tmpdir(), 'i18n-keys-'));
  try {
    writeFixture(dir, width, chain);
    const run = spawnSync(process.execPath, [VALIDATOR, '--src', dir], { encoding: 'utf8' });
    return { status: run.status, output: `${run.stdout ?? ''}${run.stderr ?? ''}` };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('a candidate list above the cap fails the run instead of passing', () => {
  const { status, output } = runOn(251, 24);
  assert.equal(status, 1, `expected a failing run, got ${status}:\n${output}`);
  assert.doesNotMatch(output, /PASS/, output);
  assert.match(output, /51 dropped/, output);
  assert.match(output, /lookup-table\.ts:\d+/, output);
});

test('every cap reports its own cause', () => {
  const { output } = runOn(251, 24);
  for (const cause of CAUSES) {
    assert.match(
      output,
      new RegExp(`${cause}: \\d+ site\\(s\\)`),
      `${cause} missing from:\n${output}`
    );
  }
});

test('the same shapes below the cap still pass', () => {
  const { status, output } = runOn(4, 3);
  assert.equal(status, 0, `expected a passing run, got ${status}:\n${output}`);
  assert.match(output, /PASS/, output);
});
