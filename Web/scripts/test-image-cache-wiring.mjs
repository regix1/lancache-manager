import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { collectNodes, findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * The image cache version has one owner. It used to have two: the Downloads tab held a counter
 * seeded from the backend generation and the Storage section held a `Date.now()` stamp, both
 * writing the single module-scope buster in `useAvailableGameImages`, so whichever subtree rendered
 * last decided whether the other's refetch was skipped, and the `GameImagesUpdated` subscription
 * only existed while the Downloads tab was mounted. These checks hold that collapse in place.
 */

const SRC = new URL('../src/', import.meta.url);
const PROVIDER_FILE = 'src/components/AppProviders.tsx';

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

const readAll = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

/** Every JSX tag in a file, or under one node, whose tag name reads exactly `name`. */
const jsxTags = (node, name) =>
  collectNodes(
    node,
    (candidate) =>
      (ts.isJsxOpeningElement(candidate) || ts.isJsxSelfClosingElement(candidate)) &&
      candidate.tagName.getText() === name
  );

/** Every call in a file whose callee reads exactly `name`. */
const callsTo = (sourceFile, name) =>
  collectNodes(
    sourceFile,
    (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === name
  );

test('exactly one ImageCacheContext.Provider is mounted, and it is in AppProviders', () => {
  const mountSites = sourceFiles.flatMap((relativePath) =>
    jsxTags(parse(relativePath), 'ImageCacheContext.Provider').map(() => relativePath)
  );
  assert.deepEqual(mountSites, [PROVIDER_FILE]);
});

test('the one provider sits below SignalRProvider and above the app', () => {
  const sourceFile = parse(PROVIDER_FILE);
  const signalR = findSoleNode(
    sourceFile,
    'SignalRProvider element',
    (node) => ts.isJsxElement(node) && node.openingElement.tagName.getText() === 'SignalRProvider'
  );
  assert.equal(jsxTags(signalR, 'ImageCacheProvider').length, 1);
});

test('the image cache version is a counter seeded from the backend, not a timestamp', () => {
  const sourceFile = parse(PROVIDER_FILE);
  const seeds = callsTo(sourceFile, 'useState').map((node) =>
    node.arguments[0]?.getText(sourceFile)
  );
  assert.deepEqual(seeds, ['0']);
  assert.equal(callsTo(sourceFile, 'Date.now').length, 0);
});

test('only the provider reads the backend cache version, and every read has a catch', () => {
  const readers = sourceFiles.filter(
    (relativePath) => callsTo(parse(relativePath), 'ApiService.getImageCacheVersion').length > 0
  );
  assert.deepEqual(readers, [PROVIDER_FILE]);

  const sourceFile = parse(PROVIDER_FILE);
  const reads = callsTo(sourceFile, 'ApiService.getImageCacheVersion');
  assert.ok(reads.length > 0, 'expected the provider to read the version at least once');

  for (const read of reads) {
    const chain = [];
    for (let node = read.parent; node; node = node.parent) {
      if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)) {
        chain.push(node.expression.name.getText(sourceFile));
      }
      if (ts.isExpressionStatement(node)) break;
    }
    assert.ok(
      chain.includes('catch'),
      `expected a catch on the version read at line ${
        sourceFile.getLineAndCharacterOfPosition(read.getStart(sourceFile)).line + 1
      }, got ${chain.join('.')}`
    );
  }
});

test('GameImagesUpdated is subscribed once, above every banner', () => {
  const subscribers = sourceFiles.filter((relativePath) =>
    readAll(relativePath).includes("on('GameImagesUpdated'")
  );
  assert.deepEqual(subscribers, [PROVIDER_FILE]);
});

test('the refresh button no longer writes the version the response captured', () => {
  const downloadsTab = readAll('src/components/features/downloads/DownloadsTab.tsx');
  assert.ok(
    !downloadsTab.includes('setImageCacheVersion'),
    'the refresh handler must leave the version to the SignalR event'
  );
  assert.ok(!downloadsTab.includes('cacheGeneration'));
});
