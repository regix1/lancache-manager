import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { collectNodes, parseSource } from './transpile-module.mjs';

/**
 * One way to recover from a dropped connection, not eighteen.
 *
 * Events emitted while a client's socket is down are never redelivered, so a view whose state comes
 * only from a push stays wrong until it remounts. `useReconnectRefetch` is the single answer, and
 * what it fires on is proved in test-reconnect-refetch.mjs. These checks stop a new hand-rolled
 * version appearing beside it: they run over the whole tree at push time, so they still catch a
 * commit that skipped the lint hook, and they catch shapes an eslint selector cannot express, such
 * as a latch ref under a name nobody thought to ban.
 */

const SRC = new URL('../src/', import.meta.url);
const HOOK_FILE = 'src/hooks/useReconnectRefetch.ts';

/** Owns the connection itself, so it is the one place allowed to read the raw state machine. */
const CONNECTION_OWNER = 'src/contexts/SignalRContext/';

/**
 * The prefill panel runs a SECOND, independent hub with its own lifetime, so the main hub's
 * `isConnected` says nothing about it and it reconciles through its own `onreconnected`. Anything
 * else registering a hub-level reconnect handler is reimplementing the shared hook.
 */
const SEPARATE_HUB_FILES = [
  'src/components/features/prefill/hooks/usePrefillEventHandlers.ts',
  'src/components/features/prefill/hooks/usePrefillSignalR.ts'
];

/**
 * Reacts to the connection going DOWN rather than to it coming back, which is the opposite trigger
 * and so cannot be expressed as a refetch callback. The activity registry holds no REST source at
 * all: it arms a flag while the socket is down so the next pushed snapshot is treated as a new
 * baseline. Add a file here only with a reason of that kind, never to quiet the check.
 */
const ARMS_WHILE_DOWN = ['src/contexts/ActivityContext/ActivityProvider.tsx'];

const sourceFiles = (() => {
  const found = [];
  const walk = (relativeDir) => {
    const dir = new URL(relativeDir, SRC);
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        walk(`${relativeDir}${entry.name}/`);
      } else if (entry.name.endsWith('.ts') || entry.name.endsWith('.tsx')) {
        found.push(`src/${relativeDir}${entry.name}`);
      }
    }
  };
  walk('');
  return found;
})();

const parse = (relativePath) =>
  parseSource(relativePath, relativePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);

/** Files where `where` finds at least one node, as `path:line` strings. */
const sitesMatching = (where, files = sourceFiles) => {
  const hits = [];
  for (const relativePath of files) {
    const sourceFile = parse(relativePath);
    for (const node of collectNodes(sourceFile, where)) {
      const line = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;
      hits.push(`${relativePath}:${line}`);
    }
  }
  return hits;
};

test('nothing outside the SignalR context compares connectionState to "connected"', () => {
  const comparisons = sitesMatching(
    (node) =>
      ts.isBinaryExpression(node) &&
      (node.operatorToken.kind === ts.SyntaxKind.EqualsEqualsEqualsToken ||
        node.operatorToken.kind === ts.SyntaxKind.ExclamationEqualsEqualsToken) &&
      ts.isStringLiteral(node.right) &&
      node.right.text === 'connected' &&
      node.left.getText().endsWith('connectionState'),
    sourceFiles.filter((path) => !path.startsWith(CONNECTION_OWNER))
  );

  assert.deepEqual(
    comparisons,
    [],
    `Read the isConnected boolean instead, and call useReconnectRefetch(isConnected, callback) to re-fetch once a dropped connection returns. Found at: ${comparisons.join(', ')}`
  );
});

test('an effect keyed only on the connection uses the hook instead of its own effect', () => {
  // Matched on SHAPE, not on names: an effect whose entire dependency list is the connection is a
  // reconnect reaction, whatever its refs are called. That is the hook's job, so writing the effect
  // by hand is the duplicate this guards against. Naming the shape rather than a banned-word list is
  // what stops it being sidestepped by picking a different variable name.
  const CONNECTION_DEPS = new Set(['isConnected', 'connectionState']);

  const handRolled = sitesMatching(
    (node) => {
      if (!ts.isCallExpression(node) || node.expression.getText() !== 'useEffect') {
        return false;
      }
      const deps = node.arguments[1];
      if (deps === undefined || !ts.isArrayLiteralExpression(deps)) {
        return false;
      }
      return (
        deps.elements.length === 1 &&
        CONNECTION_DEPS.has(deps.elements[0].getText().replace(/^signalR\./, ''))
      );
    },
    sourceFiles.filter(
      (path) =>
        path !== HOOK_FILE &&
        !path.startsWith(CONNECTION_OWNER) &&
        !SEPARATE_HUB_FILES.includes(path) &&
        !ARMS_WHILE_DOWN.includes(path)
    )
  );

  assert.deepEqual(
    handRolled,
    [],
    `Call useReconnectRefetch(isConnected, callback) rather than writing the effect. If the effect genuinely reacts to the connection going DOWN rather than coming back, add its file to ARMS_WHILE_DOWN with the reason. Found at: ${handRolled.join(', ')}`
  );
});

test('only the two files running a separate hub register their own reconnect handler', () => {
  const handlers = sitesMatching(
    (node) =>
      ts.isCallExpression(node) &&
      ts.isPropertyAccessExpression(node.expression) &&
      ['onreconnected', 'onclose'].includes(node.expression.name.getText()),
    sourceFiles.filter((path) => !path.startsWith(CONNECTION_OWNER))
  );

  const unexpected = handlers.filter(
    (site) => !SEPARATE_HUB_FILES.some((allowed) => site.startsWith(`${allowed}:`))
  );
  assert.deepEqual(
    unexpected,
    [],
    `A hub-level reconnect handler outside the SignalR context reimplements useReconnectRefetch. Found at: ${unexpected.join(', ')}`
  );
});

test('every file that imports the hook actually calls it', () => {
  const importers = sourceFiles.filter((relativePath) => {
    const sourceFile = parse(relativePath);
    return (
      collectNodes(
        sourceFile,
        (node) =>
          ts.isImportDeclaration(node) &&
          node.moduleSpecifier.getText(sourceFile).includes('useReconnectRefetch')
      ).length > 0
    );
  });

  assert.ok(importers.length > 0, 'expected the shared hook to have importers');

  const dead = importers.filter((relativePath) => {
    const sourceFile = parse(relativePath);
    return (
      collectNodes(
        sourceFile,
        (node) =>
          ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useReconnectRefetch'
      ).length === 0
    );
  });

  assert.deepEqual(dead, [], `imported but never called in: ${dead.join(', ')}`);
});
