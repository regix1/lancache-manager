import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * The dashboard asks the server to group the hourly buckets on the clock the reader is looking at,
 * and draws the "now" marker over those buckets on the same clock, so a clock change has to move
 * both. The effect is lifted out of the product source and run here rather than restated, so
 * narrowing its guard or dropping the clock from its dependency list fails this file.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const dashboardSource = readWebSource('src/contexts/DashboardDataContext/index.tsx');
const apiServiceSource = readWebSource('src/services/api.service.ts');

const dashboardFile = ts.createSourceFile(
  'index.tsx',
  dashboardSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

const only = (nodes, description) => {
  assert.equal(nodes.length, 1, description);
  return nodes[0];
};

/** The initializer of a named `const`, as source text. */
const initializerOf = (sourceFile, name) => {
  const declaration = only(
    collect(
      sourceFile,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
    ),
    `expected exactly one ${name} declaration in ${sourceFile.fileName}`
  );
  assert.ok(declaration.initializer, `${name} has no initializer`);
  return declaration.initializer.getText(sourceFile);
};

/** The one `useEffect` whose body mentions the given text, as its callback and dependency texts. */
const effectMentioning = (sourceFile, marker) => {
  const call = only(
    collect(
      sourceFile,
      (node) =>
        ts.isCallExpression(node) &&
        node.expression.getText(sourceFile) === 'useEffect' &&
        node.arguments.length === 2 &&
        node.arguments[0].getText(sourceFile).includes(marker)
    ),
    `expected exactly one useEffect mentioning ${marker}`
  );
  return {
    callback: call.arguments[0].getText(sourceFile),
    dependencies: call.arguments[1]
      .getText(sourceFile)
      .replace(/^\[|\]$/g, '')
      .split(',')
      .map((entry) => entry.trim())
      .filter(Boolean)
  };
};

const clockEffect = effectMentioning(dashboardFile, 'clockChange');

/** Runs the lifted effect callback with the values it reads supplied by name. */
const runClockEffect = (bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${clockEffect.callback});`);
  return new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  )();
};

const withZone = ({ readerZone, previousZone, mockMode = false, hasAccess = true }) => {
  const calls = [];
  const prevReaderZoneRef = { current: previousZone };
  runClockEffect({
    readerZone,
    prevReaderZoneRef,
    mockMode,
    hasAccess,
    fetchAllData: (options) => calls.push(options)
  });
  return { calls, prevReaderZoneRef };
};

test('the clock the refetch compares is the clock the request sends and the marker reads', () => {
  assert.equal(
    initializerOf(dashboardFile, 'readerZone'),
    'getEffectiveTimezone()',
    'the provider must read the reader clock through the shared preference, with no arguments'
  );
  assert.match(
    apiServiceSource,
    /params\.append\(\s*'timeZoneId',\s*getEffectiveTimezone\(\)\s*\)/,
    'the batch request must send the zone name from that same call'
  );
});

test('the reader clock is a dependency of the effect that refetches', () => {
  assert.ok(
    clockEffect.dependencies.includes('readerZone'),
    `readerZone missing from [${clockEffect.dependencies.join(', ')}], so a clock change never re-runs the effect`
  );
});

test('an unchanged clock does not refetch', () => {
  const { calls } = withZone({ readerZone: 'Europe/Berlin', previousZone: 'Europe/Berlin' });
  assert.deepEqual(calls, []);
});

test('a changed clock refetches once and records the clock it fetched on', () => {
  const { calls, prevReaderZoneRef } = withZone({
    readerZone: 'UTC',
    previousZone: 'Europe/Berlin'
  });

  assert.equal(calls.length, 1);
  assert.equal(calls[0].showLoading, false);
  assert.equal(calls[0].trigger, 'clockChange:UTC');
  assert.equal(prevReaderZoneRef.current, 'UTC');
});

test('the refetch is forced, so it supersedes a batch that is still in flight', () => {
  // A plain call returns at the 250ms debounce or at the concurrent-fetch guard, which would leave
  // the in-flight batch, grouped on the clock the reader just left, as the final state on screen.
  const { calls } = withZone({ readerZone: 'UTC', previousZone: 'Europe/Berlin' });
  assert.equal(calls[0].forceRefresh, true);
});

test('a clock that changed once does not keep refetching', () => {
  const calls = [];
  const prevReaderZoneRef = { current: 'Europe/Berlin' };
  const bindings = {
    readerZone: 'UTC',
    prevReaderZoneRef,
    mockMode: false,
    hasAccess: true,
    fetchAllData: (options) => calls.push(options)
  };
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${clockEffect.callback});`);
  const effect = new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  );

  effect();
  effect();

  assert.equal(calls.length, 1);
});

test('mock mode and a session without access never refetch', () => {
  assert.deepEqual(
    withZone({ readerZone: 'UTC', previousZone: 'Europe/Berlin', mockMode: true }).calls,
    []
  );
  assert.deepEqual(
    withZone({ readerZone: 'UTC', previousZone: 'Europe/Berlin', hasAccess: false }).calls,
    []
  );
});
