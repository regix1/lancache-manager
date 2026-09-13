import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import ts from 'typescript';
import { checkTimestamps } from './validate-timestamps.mjs';

const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../src');
const rejected = {
  'native.tsx': 'const value = <span>{new Date().toLocaleString()}</span>;',
  'date-only.ts': 'function label(date: Date) { return date.toLocaleDateString(); }',
  'time-only.ts': 'const date = new Date(); const label = date.toLocaleTimeString();',
  'optional.ts': 'function label(date?: Date) { return date?.toLocaleString(); }',
  'bracket.ts': 'const date = new Date(); const label = date["toLocaleString"]();',
  'computed.ts':
    'const date = new Date(); const method = "toLocaleString"; const label = date[method]();',
  'extracted.ts': 'const date = new Date(); const format = date.toLocaleString.bind(date);',
  'date-string.ts': 'const label = new Date().toString();',
  'utc-string.ts': 'const label = new Date().toUTCString();',
  'date-call.ts': 'const label = Date();',
  'intl.ts': 'const format = new Intl.DateTimeFormat("en-US");',
  'intl-call.ts': 'const format = Intl.DateTimeFormat();',
  'intl-alias.ts': 'const Format = Intl.DateTimeFormat; const format = new Format();',
  'intl-method.ts':
    'function label(format: Intl.DateTimeFormat, date: Date) { return format.format(date); }',
  'dateTimeFormat.ts': 'const label = new Date().toLocaleString();'
};
const allowed = {
  'numbers.ts': 'const label = (12345).toLocaleString(); const id = (42).toString();',
  'strings.ts': 'const label = "hello".toString();',
  'own-method.ts':
    'const entry = { toLocaleString: () => "label" }; const label = entry.toLocaleString();',
  'arithmetic.ts':
    'const elapsed = Date.now() - new Date().getTime(); const parsed = Date.parse("2026-09-13"); const utc = Date.UTC(2026, 8, 13);',
  'serialization.ts':
    'const request = { startedAt: new Date().toISOString() }; const json = JSON.stringify(request);',
  'zone.ts': 'const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;',
  'zone-new.ts': 'const zone = new Intl.DateTimeFormat().resolvedOptions().timeZone;',
  'wall-clock.ts': 'const date = new Date(); const month = date.getMonth(); date.setHours(9);',
  'shared.tsx':
    'declare function FormattedTimestamp(props: { timestamp: string }): unknown; const label = <FormattedTimestamp timestamp="2026-09-13T23:42:13Z" />;'
};
const sources = new Map([
  ...Object.entries({ ...rejected, ...allowed }).map(([name, source]) => [
    resolve(srcRoot, 'components', name),
    `${source}\nexport {};`
  ]),
  [
    resolve(srcRoot, 'utils/dateTimeFormat.ts'),
    'export const label = new Date().toLocaleString();'
  ],
  [resolve(srcRoot, 'utils/timezone.ts'), 'export const zone = new Intl.DateTimeFormat();']
]);
const options = {
  target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext,
  jsx: ts.JsxEmit.Preserve,
  types: [],
  noEmit: true
};
const host = ts.createCompilerHost(options);
const getSourceFile = host.getSourceFile.bind(host);
host.getSourceFile = (file, languageVersion, onError, shouldCreateNewSourceFile) =>
  sources.has(resolve(file))
    ? ts.createSourceFile(file, sources.get(resolve(file)), languageVersion, true)
    : getSourceFile(file, languageVersion, onError, shouldCreateNewSourceFile);
const program = ts.createProgram([...sources.keys()], options, host);
const errors = checkTimestamps(program, srcRoot);

for (const name of Object.keys(rejected)) {
  test(`timestamp lint rejects ${name}`, () => {
    const found = errors.filter((error) => error.file === `components/${name}`);
    assert.ok(found.length > 0, 'native timestamp formatting must fail the gate');
    assert.ok(found.every((error) => error.line > 0 && error.column > 0));
    assert.ok(found.every((error) => error.message.includes('useFormattedDateTime')));
  });
}

for (const name of Object.keys(allowed)) {
  test(`timestamp lint allows ${name}`, () => {
    assert.deepEqual(
      errors.filter((error) => error.file === `components/${name}`),
      []
    );
  });
}

test('only the shared clock utilities own native timestamp formatting', () => {
  assert.deepEqual(
    errors.filter((error) => error.file.startsWith('utils/')),
    []
  );
});

test('timestamp validation is required by lint and pre-push', () => {
  const scripts = JSON.parse(
    readFileSync(new URL('../package.json', import.meta.url), 'utf8')
  ).scripts;
  assert.equal(scripts['validate:timestamps'], 'node scripts/validate-timestamps.mjs');
  assert.match(scripts.lint, /npm run validate:timestamps/);
  assert.match(
    readFileSync(new URL('./git-hooks/pre-push', import.meta.url), 'utf8'),
    /npm run validate:timestamps/
  );
});
