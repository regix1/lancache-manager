import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * The Downloads page asks the server for one page at a time, so the page number and the filters
 * travel in the same request. Narrow the list without moving the page number back and the request
 * asks for page 7 of a list that is now three pages long: the reader is looking at rows that are
 * nowhere near the ones page 1 would show, and nothing on screen says the filter moved them.
 *
 * The clamp elsewhere in the file only rescues a page past the END of the list. Page 7 of a
 * 12-page result stays exactly where it is, which is why the reset below has to name every filter
 * the fetch sends.
 */

const downloadsTab = parseSource(
  'src/components/features/downloads/DownloadsTab.tsx',
  ts.ScriptKind.TSX
);

const pageFetch = findSoleNode(
  downloadsTab,
  'the useRetroDownloads call that fetches the page',
  (node) =>
    ts.isCallExpression(node) && node.expression.getText(downloadsTab) === 'useRetroDownloads'
);

const resetEffect = findSoleNode(
  downloadsTab,
  'the useEffect that sends the reader back to page 1',
  (node) =>
    ts.isCallExpression(node) &&
    node.expression.getText(downloadsTab) === 'useEffect' &&
    node.arguments.length === 2 &&
    node.arguments[0].getText(downloadsTab).includes('setCurrentPage(1)')
);

/** Every `settings.x` the page fetch reads, deduplicated. */
const fetchedSettings = [
  ...new Set(pageFetch.arguments[0].getText(downloadsTab).match(/settings\.[A-Za-z]+/g) ?? [])
];

/** Source text of each entry in the reset effect's dependency array. */
const resetDependencies = resetEffect.arguments[1].elements.map((element) =>
  element.getText(downloadsTab)
);

test('the page fetch is driven by the toolbar settings', () => {
  // The test below reads its expectation out of the fetch, so it would pass on an empty list.
  // These two are the ones that were sent without a reset, and they anchor it.
  assert.ok(
    fetchedSettings.includes('settings.hideEvicted'),
    'the evicted checkbox has to reach the page fetch'
  );
  assert.ok(
    fetchedSettings.includes('settings.hitMissFilter'),
    'the hit/miss control has to reach the page fetch'
  );
});

test('every filter the page fetch sends puts the reader back on page 1', () => {
  const missing = fetchedSettings.filter((setting) => !resetDependencies.includes(setting));
  assert.deepEqual(
    missing,
    [],
    `these change which rows the server returns but leave the page number where it was: ${missing.join(', ')}`
  );
});
