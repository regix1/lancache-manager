import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * A hub connection recovers two ways: the client's own retry, which reports `onreconnected`, and a
 * full close followed by a fresh connection, which reports nothing. The dashboard has to pull a new
 * batch either way, so its recovery has to hang off the flag both paths raise. This file holds it
 * there: it checks the signal the dashboard listens to, checks both paths in the connection raise
 * that signal, and runs the dashboard's own recovery callback to confirm what it asks for.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const dashboardSource = readWebSource('src/contexts/DashboardDataContext/index.tsx');
const signalRSource = readWebSource('src/contexts/SignalRContext/index.tsx');

const parse = (fileName, source) =>
  ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);

const dashboardFile = parse('index.tsx', dashboardSource);
const signalRFile = parse('SignalRContext.tsx', signalRSource);

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

const callsTo = (sourceFile, name) =>
  collect(
    sourceFile,
    (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === name
  );

/** Walks up from a node to the nearest enclosing call, e.g. the handler a callback was passed to. */
const enclosingCallNames = (node, sourceFile) => {
  const names = [];
  for (let current = node.parent; current; current = current.parent) {
    if (ts.isCallExpression(current)) {
      names.push(current.expression.getText(sourceFile));
    }
  }
  return names;
};

test('the dashboard recovers off the connection flag, not a reconnect event', () => {
  const resync = callsTo(dashboardFile, 'useReconnectRefetch');
  assert.equal(resync.length, 1, 'expected exactly one reconnect resync in the dashboard');
  assert.equal(
    resync[0].arguments[0].getText(dashboardFile),
    'signalR.isConnected',
    'a reconnect event is raised on one recovery path only, so it cannot drive this'
  );
});

test('the retired reconnect event is gone from the app', () => {
  const walk = (directory) =>
    readdirSync(directory).flatMap((entry) => {
      const path = `${directory}/${entry}`;
      return statSync(path).isDirectory() ? walk(path) : [path];
    });

  const sourceRoot = new URL('../src', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
  const offenders = walk(sourceRoot)
    .filter((path) => /\.tsx?$/.test(path))
    .filter((path) => readFileSync(path, 'utf8').includes('SIGNALR_RECONNECTED'));

  assert.deepEqual(offenders, [], 'two signals that have to agree is what left the cold path out');
});

test('both recovery paths raise the flag the dashboard watches', () => {
  const raised = callsTo(signalRFile, 'setIsConnected').filter(
    (call) => call.arguments[0].getText(signalRFile) === 'true'
  );

  assert.equal(raised.length, 2, 'expected the retry path and the fresh-connection path');
  assert.ok(
    raised.some((call) =>
      enclosingCallNames(call, signalRFile).includes('connection.onreconnected')
    ),
    'the client retry path must set it'
  );
  assert.ok(
    raised.some(
      (call) => !enclosingCallNames(call, signalRFile).includes('connection.onreconnected')
    ),
    'the close-then-fresh-connection path must set it too'
  );
});

/** Runs the dashboard's recovery callback with the values it reads supplied by name. */
const runRecovery = ({ mockMode = false, hasAccess = true } = {}) => {
  const callback = callsTo(dashboardFile, 'useReconnectRefetch')[0].arguments[1].getText(
    dashboardFile
  );
  const fetches = [];
  const bindings = {
    mockMode,
    hasAccess,
    fetchAllData: (options) => fetches.push(options)
  };
  const names = Object.keys(bindings);
  const compiled = transpile(`const lifted = (${callback});`);
  new Function(...names, `${compiled}\nreturn lifted;`)(...names.map((name) => bindings[name]))();
  return fetches;
};

test('recovery pulls one forced batch without a loading wipe', () => {
  const fetches = runRecovery();

  assert.equal(fetches.length, 1);
  assert.equal(fetches[0].forceRefresh, true, 'a plain call is dropped by the debounce');
  assert.equal(fetches[0].showLoading, false, 'the visible numbers stay up until the batch lands');
  assert.equal(fetches[0].trigger, 'signalr-reconnected');
});

test('mock mode and a session without access never pull a batch', () => {
  assert.deepEqual(runRecovery({ mockMode: true }), []);
  assert.deepEqual(runRecovery({ hasAccess: false }), []);
});
