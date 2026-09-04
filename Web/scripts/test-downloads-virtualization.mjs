import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { Virtualizer } from '@tanstack/react-virtual';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * The three grouped Downloads views hand their rows to a virtualizer once the list passes a
 * threshold, so the browser holds a screenful of nodes instead of the whole list. That threshold is
 * 200 and the largest numbered page size is also 200, which left the code switched off for as long
 * as a page was the most the reader could ask for. Restoring "All" switches it back on, so this
 * file drives the shipped configuration rather than trusting it.
 *
 * The options each view hands `useVirtualizer`, the flags that decide whether it runs at all, the
 * row flattening they index over and the scroll reset are all lifted out of the files that ship. A
 * real `Virtualizer` from the library the app already depends on is then driven headlessly, with a
 * stand-in scroll box supplying the rect and offset a browser would.
 */

const COMPACT_VIEW = 'src/components/features/downloads/CompactView.tsx';
const NORMAL_VIEW = 'src/components/features/downloads/NormalView.tsx';
const FLAT_ROWS = 'src/hooks/useFlatRows.ts';
const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const compactView = parseSource(COMPACT_VIEW, ts.ScriptKind.TSX);
const normalView = parseSource(NORMAL_VIEW, ts.ScriptKind.TSX);
const downloadsTab = parseSource(DOWNLOADS_TAB, ts.ScriptKind.TSX);

/** Source text of the value a named `const` is declared with. */
const initializerOf = (sourceFile, name) =>
  findSoleNode(
    sourceFile,
    `the ${name} declaration`,
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === name &&
      node.initializer !== undefined
  ).initializer.getText(sourceFile);

/** Runs a lifted expression with its free variables supplied and hands back what it evaluates to. */
const evaluate = (source, bindings) => bindLifted(`() => (${source})`, bindings)();

/** Source text of the dependency array of the `useEffect` whose body contains `contains`. */
const effectDeps = (sourceFile, contains) =>
  findSoleNode(
    sourceFile,
    `useEffect for ${contains}`,
    (node) =>
      ts.isCallExpression(node) &&
      node.expression.getText(sourceFile) === 'useEffect' &&
      node.arguments.length === 2 &&
      node.arguments[0].getText(sourceFile).includes(contains)
  ).arguments[1].getText(sourceFile);

/** The options object a view hands `useVirtualizer`, picked out by a name only that one call reads. */
const virtualizerOptions = (sourceFile, reads, bindings) => {
  const calls = collectNodes(
    sourceFile,
    (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useVirtualizer'
  ).filter((node) => node.arguments[0].getText(sourceFile).includes(reads));
  assert.equal(
    calls.length,
    1,
    `expected one useVirtualizer reading ${reads} in ${sourceFile.fileName}`
  );
  return evaluate(calls[0].arguments[0].getText(sourceFile), bindings);
};

const COMPACT_THRESHOLD = Number(initializerOf(compactView, 'VIRTUALIZATION_THRESHOLD'));
const NORMAL_THRESHOLD = Number(initializerOf(normalView, 'VIRTUALIZATION_THRESHOLD'));

/** The row flattening the virtualizers index over, lifted out of the hook. */
const flatRowsOf = (items, groupByFrequency) =>
  bindLifted(liftHookCallback(FLAT_ROWS, 'useMemo', 'multipleHeaderEmitted'), {
    items,
    groupByFrequency
  })();

const group = (index, count = 3) => ({
  id: `game-appid-${index}`,
  name: `Game ${index}`,
  count,
  downloads: [],
  totalBytes: 1000
});

const manyGroups = (howMany) => Array.from({ length: howMany }, (unused, index) => group(index));

/**
 * A real virtualizer running the shipped options against a stand-in scroll box, so the row window
 * it produces is the one a browser would get.
 *
 * @param {object} options The options object lifted out of the view.
 * @param {number} viewportHeight How tall the scrolling box is.
 * @returns {{ virtualizer: Virtualizer, scrollTo: (offset: number) => void, window: () => object[] }}
 */
const mount = (options, viewportHeight = 800) => {
  const scrollBox = { scrollTop: 0, clientHeight: viewportHeight, clientWidth: 1200 };
  let reportOffset = null;
  const virtualizer = new Virtualizer({
    ...options,
    observeElementRect: (instance, report) => report({ width: 1200, height: viewportHeight }),
    observeElementOffset: (instance, report) => {
      reportOffset = report;
      report(scrollBox.scrollTop, false);
    },
    scrollToFn: (offset) => {
      scrollBox.scrollTop = offset;
    }
  });
  virtualizer._didMount();
  virtualizer._willUpdate();
  return {
    virtualizer,
    scrollBox,
    scrollTo: (offset) => {
      scrollBox.scrollTop = offset;
      reportOffset(offset, false);
      virtualizer._willUpdate();
    },
    window: () => {
      virtualizer._willUpdate();
      return virtualizer.getVirtualItems();
    }
  };
};

/** A rendered row, the way the views hand one to `measureElement`. */
const renderedRow = (index, height) => ({
  getAttribute: () => String(index),
  getBoundingClientRect: () => ({ height })
});

// -- the threshold, and why All is what switches it on -------------------------------------------

test('the largest numbered page size is the threshold, so only All virtualizes', () => {
  const sizes = bindLifted(
    liftHookCallback(DOWNLOADS_TAB, 'useMemo', 'downloads.tab.filters.allItems'),
    {
      settings: { viewMode: 'normal' },
      t: (key) => key,
      ALL_ITEMS_PAGE_SIZE: 0
    }
  )()
    .map((option) => Number(option.value))
    .filter((size) => size > 0);

  assert.equal(Math.max(...sizes), COMPACT_THRESHOLD);
  assert.equal(Math.max(...sizes), NORMAL_THRESHOLD);
});

test('a full numbered page does not virtualize and one row more does', () => {
  const compact = (rowCount) =>
    evaluate(initializerOf(compactView, 'shouldVirtualize'), {
      flatRows: { length: rowCount },
      VIRTUALIZATION_THRESHOLD: COMPACT_THRESHOLD
    });
  assert.equal(compact(COMPACT_THRESHOLD), false);
  assert.equal(compact(COMPACT_THRESHOLD + 1), true);

  const normalList = (rowCount, cardGridLayout) =>
    evaluate(initializerOf(normalView, 'shouldVirtualizeList'), {
      cardGridLayout,
      flatRows: { length: rowCount },
      VIRTUALIZATION_THRESHOLD: NORMAL_THRESHOLD
    });
  assert.equal(normalList(NORMAL_THRESHOLD, false), false);
  assert.equal(normalList(NORMAL_THRESHOLD + 1, false), true);
  assert.equal(normalList(NORMAL_THRESHOLD + 1, true), false, 'the card grid has its own path');

  const normalGrid = (itemCount, cardGridLayout) =>
    evaluate(initializerOf(normalView, 'shouldVirtualizeGrid'), {
      cardGridLayout,
      items: { length: itemCount },
      VIRTUALIZATION_THRESHOLD: NORMAL_THRESHOLD
    });
  assert.equal(normalGrid(NORMAL_THRESHOLD, true), false);
  assert.equal(normalGrid(NORMAL_THRESHOLD + 1, true), true);
});

// -- the compact list ----------------------------------------------------------------------------

test('twenty thousand compact rows render a screenful, not twenty thousand', () => {
  const flatRows = flatRowsOf(manyGroups(20000), false);
  assert.equal(flatRows.length, 20000);

  const virtualParentRef = { current: null };
  const options = virtualizerOptions(compactView, 'shouldVirtualize', {
    shouldVirtualize: true,
    flatRows,
    virtualParentRef
  });
  assert.equal(options.count, 20000);

  const list = mount(options);
  virtualParentRef.current = list.scrollBox;
  const rows = list.window();

  assert.ok(rows.length < 40, `a screenful of 48px rows, not the list (${rows.length} rendered)`);
  const last = rows[rows.length - 1];
  assert.ok(
    last.start + last.size >= list.scrollBox.clientHeight,
    'and enough of them to cover the box the reader is looking at'
  );
  assert.equal(rows[0].index, 0);
});

test('scrolling deep into the list moves the window instead of growing it', () => {
  const flatRows = flatRowsOf(manyGroups(20000), false);
  const virtualParentRef = { current: null };
  const list = mount(
    virtualizerOptions(compactView, 'shouldVirtualize', {
      shouldVirtualize: true,
      flatRows,
      virtualParentRef
    })
  );
  virtualParentRef.current = list.scrollBox;
  list.window();

  list.scrollTo(48 * 15000);
  const rows = list.window();

  assert.ok(rows.length < 40, `still a screenful (${rows.length} rendered)`);
  assert.ok(rows[0].index > 14000, `the window followed the scroll (first row ${rows[0].index})`);
  assert.ok(rows[rows.length - 1].index < 20000, 'and never past the end of the list');
});

// A collapsed group is one row tall; opening it is several. The estimate is 48 either way, so the
// rows below an open group sit at the wrong offset until the real height is measured back in.
test('an opened group is measured back in and moves the rows below it', () => {
  const flatRows = flatRowsOf(manyGroups(20000), false);
  const virtualParentRef = { current: null };
  const list = mount(
    virtualizerOptions(compactView, 'shouldVirtualize', {
      shouldVirtualize: true,
      flatRows,
      virtualParentRef
    })
  );
  virtualParentRef.current = list.scrollBox;

  const before = list.window();
  const estimated = before[3].size;
  const followedAt = before[4].start;

  list.virtualizer.measureElement(renderedRow(3, 620));
  const after = list.window();

  assert.equal(after[3].size, 620, 'the open group is as tall as it rendered');
  assert.equal(
    after[4].start,
    followedAt + (620 - estimated),
    'and the row under it moved down by exactly the difference'
  );
});

test('a section header is estimated shorter than a row', () => {
  const mixed = [group(0, 4), group(1, 1)];
  const flatRows = flatRowsOf(mixed, true);
  assert.deepEqual(
    flatRows.map((row) => row.kind),
    ['header', 'item', 'header', 'item'],
    'a bucket header is a row of its own for the virtualizer to index'
  );

  const virtualParentRef = { current: null };
  const options = virtualizerOptions(compactView, 'shouldVirtualize', {
    shouldVirtualize: true,
    flatRows,
    virtualParentRef
  });
  assert.ok(
    options.estimateSize(0) < options.estimateSize(1),
    'the header estimate is the shorter of the two'
  );
});

// -- the normal list and the card grid -----------------------------------------------------------

test('twenty thousand normal rows render a screenful', () => {
  const flatRows = flatRowsOf(manyGroups(20000), false);
  const virtualParentRef = { current: null };
  const list = mount(
    virtualizerOptions(normalView, 'shouldVirtualizeList', {
      shouldVirtualizeList: true,
      flatRows,
      virtualParentRef
    })
  );
  virtualParentRef.current = list.scrollBox;
  const rows = list.window();

  assert.ok(rows.length < 25, `a screenful of 240px cards (${rows.length} rendered)`);
  const last = rows[rows.length - 1];
  assert.ok(last.start + last.size >= list.scrollBox.clientHeight);
});

// The card grid is a second virtualizer over chunked rows, not the list one with different styling,
// so it has to be exercised on its own.
test('the card grid chunks the cards into rows and virtualizes those', () => {
  const items = manyGroups(20000);
  const gridRowGroups = bindLifted(
    liftHookCallback(NORMAL_VIEW, 'useMemo', 'items.slice(i, i + cols)'),
    {
      shouldVirtualizeGrid: true,
      items,
      gridCols: 4
    }
  )();
  assert.equal(gridRowGroups.length, 5000, 'four cards to a row');
  assert.equal(gridRowGroups[0].length, 4);

  const gridParentRef = { current: null };
  const grid = mount(
    virtualizerOptions(normalView, 'shouldVirtualizeGrid', {
      shouldVirtualizeGrid: true,
      gridRowGroups,
      gridParentRef
    })
  );
  gridParentRef.current = grid.scrollBox;
  const rows = grid.window();

  assert.ok(rows.length < 20, `a screenful of card rows (${rows.length} rendered)`);

  grid.scrollTo(320 * 2500);
  const scrolled = grid.window();
  assert.ok(
    scrolled.length < 20,
    `still a screenful after scrolling (${scrolled.length} rendered)`
  );
  assert.ok(scrolled[0].index > 2400, 'and the window followed');
});

// -- the scroll reset ----------------------------------------------------------------------------

test('a changed row set puts every virtualized list back at the top', () => {
  const scrolled = { scrollTop: 4200 };
  bindLifted(liftHookCallback(COMPACT_VIEW, 'useEffect', 'virtualParentRef.current.scrollTop'), {
    virtualParentRef: { current: scrolled }
  })();
  assert.equal(scrolled.scrollTop, 0, 'the compact list');

  const scrolledNormal = { scrollTop: 4200 };
  bindLifted(liftHookCallback(NORMAL_VIEW, 'useEffect', 'virtualParentRef.current.scrollTop'), {
    virtualParentRef: { current: scrolledNormal }
  })();
  assert.equal(scrolledNormal.scrollTop, 0, 'the normal list');

  const scrolledGrid = { scrollTop: 4200 };
  bindLifted(liftHookCallback(NORMAL_VIEW, 'useEffect', 'gridParentRef.current.scrollTop'), {
    gridParentRef: { current: scrolledGrid }
  })();
  assert.equal(scrolledGrid.scrollTop, 0, 'the card grid');
});

// Two things hand every view a brand new row array holding rows it already had: opening a group,
// which refetches its sessions, and a running download, which makes the server reorder the page
// every few seconds. Keying the reset on those rows threw a reader far down an All-sized list back
// to the top on both. It now keys on what the READER chose instead, so neither can move them.
test('the scroll reset ignores the rows entirely', () => {
  assert.equal(effectDeps(compactView, 'virtualParentRef.current.scrollTop'), '[scrollResetKey]');
  assert.equal(effectDeps(normalView, 'virtualParentRef.current.scrollTop'), '[scrollResetKey]');
  assert.equal(effectDeps(normalView, 'gridParentRef.current.scrollTop'), '[scrollResetKey]');

  for (const [label, source] of [
    ['the compact list', compactView],
    ['the normal list', normalView]
  ]) {
    assert.ok(
      !source.getFullText().includes('flatRows.map((row) => row.id)'),
      `${label} no longer builds a reset key out of its rows`
    );
  }
});

// The other half of the same rule: the key still has to change when the reader picks a different
// filter, sort, page size or page, or a stale offset survives into a list it does not belong to.
test('the scroll reset key carries every choice the reader can make', () => {
  const key = initializerOf(downloadsTab, 'scrollResetKey');

  for (const field of [
    'settings.selectedService',
    'serverClientFilter',
    'debouncedSearchQuery',
    'settings.sortOrder',
    'settings.hitMissFilter',
    'settings.groupByFrequency',
    'settings.itemsPerPage',
    'currentPage'
  ]) {
    assert.ok(key.includes(field), `scrollResetKey carries ${field}`);
  }

  // Nothing derived from the rows may creep back in, which is what made it reset on a reorder.
  for (const rowish of ['flatRows', 'itemsToDisplay', 'serverPage.items', 'expandedItem']) {
    assert.ok(!key.includes(rowish), `scrollResetKey stays clear of ${rowish}`);
  }
});
