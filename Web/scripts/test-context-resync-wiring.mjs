import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * Five contexts that carry labels and settings rather than gates: an event list, download tags,
 * client hostnames, the server config and the Steam Web API key status. None of them refetches on
 * its own, so each hangs its recovery off the one shared hook, and what a hook fires on is proved
 * once against that hook in test-reconnect-refetch.mjs. What is left per context is which call it
 * makes and who it declines to make it for, so that is what runs here: the recovery callback each
 * one passes, lifted out of its own source and executed.
 */

const parse = (relativePath) => {
  const source = readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');
  return ts.createSourceFile(relativePath, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
};

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

/** The single `useReconnectRefetch` call in a file, with its two arguments as source text. */
const resyncCall = (relativePath) => {
  const sourceFile = parse(relativePath);
  const calls = collect(
    sourceFile,
    (node) =>
      ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useReconnectRefetch'
  );
  assert.equal(calls.length, 1, `expected exactly one reconnect resync in ${relativePath}`);
  return {
    flag: calls[0].arguments[0].getText(sourceFile),
    callback: calls[0].arguments[1].getText(sourceFile)
  };
};

/** Runs a recovery callback with the values it reads supplied by name. */
const runRecovery = (callback, bindings) => {
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${callback});`, ts.ModuleKind.CommonJS);
  new Function(...names, `${compiled}\nreturn lifted;`)(...names.map((name) => bindings[name]))();
};

const contexts = [
  'src/contexts/EventContext.tsx',
  'src/contexts/DownloadAssociationsContext.tsx',
  'src/contexts/ClientHostnameContext.tsx',
  'src/contexts/ConfigContext.tsx',
  'src/hooks/useSteamWebApiStatus.ts'
];

test('all five recover off the connection flag', () => {
  for (const relativePath of contexts) {
    assert.equal(resyncCall(relativePath).flag, 'isConnected', relativePath);
  }
});

test('the event list reloads, except with no server behind it and no session', () => {
  const { callback } = resyncCall('src/contexts/EventContext.tsx');
  const run = (options) => {
    const reloads = [];
    runRecovery(callback, {
      mockMode: false,
      hasAccess: true,
      refreshEvents: () => reloads.push(true),
      ...options
    });
    return reloads.length;
  };

  assert.equal(run({}), 1);
  assert.equal(run({ mockMode: true }), 0, 'mock data has no server to ask');
  assert.equal(run({ hasAccess: false }), 0, 'the list endpoints need a session');
});

test('download tags drop their cache so the visible rows are asked for again', () => {
  const { callback } = resyncCall('src/contexts/DownloadAssociationsContext.tsx');
  const fetchedIds = { current: new Set([11, 12]) };
  let version = 3;

  runRecovery(callback, {
    fetchedIds,
    setRefreshVersion: (next) => {
      version = next(version);
    }
  });

  assert.equal(fetchedIds.current.size, 0, 'a tag missed while down is cached wrong until cleared');
  assert.equal(version, 4, 'the bump is what makes the rows ask again');
});

test('hostnames reload for a signed-in viewer only', () => {
  const { callback } = resyncCall('src/contexts/ClientHostnameContext.tsx');
  const run = (authMode) => {
    const reloads = [];
    runRecovery(callback, {
      authModeRef: { current: authMode },
      refreshHostnames: () => reloads.push(true)
    });
    return reloads.length;
  };

  assert.equal(run('authenticated'), 1);
  assert.equal(run('guest'), 1, 'an admin flipping the setting changes every viewer of the labels');
  assert.equal(run('none'), 0);
});

test('the config is asked for again, through the path that keeps the cached one on failure', () => {
  const { callback } = resyncCall('src/contexts/ConfigContext.tsx');
  const reloads = [];
  runRecovery(callback, { refreshConfig: () => reloads.push(true) });

  assert.equal(reloads.length, 1);
});

test('the Steam key status reloads for an admin, without wiping the panel', () => {
  const { callback } = resyncCall('src/hooks/useSteamWebApiStatus.ts');
  const run = (hasAccess) => {
    const calls = [];
    runRecovery(callback, {
      hasAccess,
      fetchStatus: (forceRefresh, skipLoading) => calls.push({ forceRefresh, skipLoading })
    });
    return calls;
  };

  assert.deepEqual(run(true), [{ forceRefresh: false, skipLoading: true }]);
  assert.deepEqual(run(false), [], 'the endpoint 401s for anyone else');
});
