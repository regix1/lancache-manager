import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { findSoleNode, parseSource } from './transpile-module.mjs';

/**
 * The Downloads toolbar draws its filter checkboxes in every view, retro included, and the retro
 * table fetches its own page. Hide-small-files and hide-evicted used to stop at the toolbar there:
 * both boxes moved, neither changed a row, and the reader had no way to tell. Nothing in the type
 * checker catches that - the props are optional - so the two hops are pinned here.
 */

const downloadsTab = parseSource(
  'src/components/features/downloads/DownloadsTab.tsx',
  ts.ScriptKind.TSX
);
const retroView = parseSource('src/components/features/downloads/RetroView.tsx', ts.ScriptKind.TSX);

const retroElement = findSoleNode(
  downloadsTab,
  'the <RetroView> element',
  (node) => ts.isJsxSelfClosingElement(node) && node.tagName.getText(downloadsTab) === 'RetroView'
);

/** The expression a JSX prop is given, as source text. */
const propText = (name) => {
  const attribute = retroElement.attributes.properties.find(
    (property) => ts.isJsxAttribute(property) && property.name.getText(downloadsTab) === name
  );
  assert.ok(attribute, `<RetroView> is not given ${name}`);
  assert.ok(attribute.initializer, `${name} is passed with no value`);
  return attribute.initializer.getText(downloadsTab);
};

const retroFetchArgument = findSoleNode(
  retroView,
  'the useRetroDownloads call inside RetroView',
  (node) => ts.isCallExpression(node) && node.expression.getText(retroView) === 'useRetroDownloads'
).arguments[0];

/** The value a key of the retro fetch's options object is given, as source text. */
const fetchOptionText = (name) => {
  const property = retroFetchArgument.properties.find(
    (entry) => ts.isPropertyAssignment(entry) && entry.name.getText(retroView) === name
  );
  assert.ok(property, `the retro fetch is not given ${name}`);
  return property.initializer.getText(retroView);
};

test('the retro view is handed the hide-small-files and hide-evicted checkboxes', () => {
  assert.match(
    propText('filterHideSmallFiles'),
    /settings\.hideSmallFiles/,
    'the small-files checkbox has to reach the retro view'
  );
  assert.match(
    propText('filterHideEvicted'),
    /settings\.hideEvicted/,
    'the evicted checkbox has to reach the retro view'
  );
});

test('the stored evicted mode rides along with the checkbox, so changing it refetches', () => {
  assert.match(
    propText('filterHideEvicted'),
    /evictedDataMode/,
    'the server applies the stored mode already, but the fetch key has to move when it changes'
  );
});

test('the retro fetch sends both filters to the server', () => {
  assert.equal(fetchOptionText('hideSmallFiles'), 'filterHideSmallFiles');
  assert.equal(fetchOptionText('hideEvicted'), 'filterHideEvicted');
});

// The page turns its own fetch off while this table is showing, so the row count it used to read
// from that fetch has to come back from here. Without it the export button reads a count of zero
// that nothing ever updates. When that count is reported matters as much as that it is, and that
// half is driven in scripts/test-retro-count-report.mjs.
test('the retro table reports its row count back to the page', () => {
  assert.equal(propText('onTotalItemsChange'), '{setRetroTotalItems}');
});
