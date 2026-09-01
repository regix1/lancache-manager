import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * The server zone is a module value the dashboard provider reads while it renders for the first
 * time. It is written where the config lands, above the gate that holds every child back, so that
 * first read already has it. Written a render later instead, the provider seeds itself from the
 * browser's zone, corrects itself on the next render, and aborts the batch it had already sent. The
 * load is lifted out of the product source and run here rather than restated.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const configSource = readWebSource('src/contexts/ConfigContext.tsx');
const appSource = readWebSource('src/App.tsx');

const configFile = ts.createSourceFile(
  'ConfigContext.tsx',
  configSource,
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

/** The callback a named `const x = useCallback(...)` wraps, as source text. */
const callbackOf = (sourceFile, name) => {
  const declarations = collect(
    sourceFile,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
  );
  assert.equal(declarations.length, 1, `expected exactly one ${name} declaration`);

  const initializer = declarations[0].initializer;
  assert.ok(
    initializer && ts.isCallExpression(initializer),
    `${name} must be a useCallback, or the lifted body below is not the one that runs`
  );
  return initializer.arguments[0].getText(sourceFile);
};

/** Runs the lifted load with the values it reads supplied by name. */
const runLoad = (bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${callbackOf(configFile, 'loadConfig')});`);
  return new Function(...names, `${compiled}\nreturn lifted;`)(
    ...names.map((name) => bindings[name])
  )();
};

const loadWithServerZone = async (timeZone) => {
  const order = [];
  const failures = [];
  await runLoad({
    setError: (failure) => failures.push(failure),
    window: { setTimeout: () => 0, clearTimeout: (id) => id },
    CONFIG_TIMEOUT_MS: 8000,
    API_BASE: '/api',
    fetch: async () => ({ ok: true }),
    ApiService: {
      getFetchOptions: () => ({}),
      handleResponse: async () => ({ timeZone })
    },
    setServerTimezone: (zone) => order.push(['zone', zone]),
    setConfig: (config) => order.push(['config', config.timeZone]),
    configRef: { current: null },
    t: (key) => key,
    getErrorMessage: (error) => String(error)
  });
  return { order, failures };
};

test('the server zone is recorded before the config that releases the children', async () => {
  const { order, failures } = await loadWithServerZone('Europe/Berlin');

  assert.deepEqual(order, [
    ['zone', 'Europe/Berlin'],
    ['config', 'Europe/Berlin']
  ]);
  // The load clears any earlier failure and records none of its own, so the order above is the
  // success path rather than a catch block that happened to write nothing.
  assert.deepEqual(failures, [null]);
});

test('nothing renders below the config until the config is there', () => {
  const gate = configSource.indexOf('if (!config)');
  const children = configSource.indexOf('<ConfigContext.Provider');

  assert.ok(gate > 0, 'the provider must hold its children back until the config has loaded');
  assert.ok(
    gate < children,
    'the gate must sit above the children, or recording the zone with the config is too late'
  );
});

test('no later writer can move the zone after the first render', () => {
  assert.ok(
    !appSource.includes('setServerTimezone'),
    'a second write from an effect below the gate puts the wasted first batch back'
  );
});
