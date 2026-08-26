import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

/**
 * The retro page a shrinking result set leaves stranded.
 *
 * Retro is paginated server-side, and its pager only renders while there is more than one page. Sit
 * on page 30, wipe the logs, and the server answers page 30 with no rows and a total of 1: the list
 * is empty and the pager is gone, so nothing on screen can get back to page 1.
 *
 * The clamp measures against the SERVER's total. The client-side total in DownloadsTab counts a
 * different set of rows, and clamping retro against that would drag a legitimate deep page back to
 * the start, which is why the two must never be crossed.
 */

/** The effect exactly as RetroView runs it, over the state a test hands it. */
const runClamp = ({ serverMode = true, requestedPage, currentPage, totalPages }) => {
  const pageChanges = [];
  bindLifted(
    liftHookCallback(
      'src/components/features/downloads/RetroView.tsx',
      'useEffect',
      'onPageChange(totalPages)'
    ),
    {
      serverMode,
      serverRetro: { currentPage: requestedPage },
      currentPage,
      totalPages,
      onPageChange: (page) => pageChanges.push(page)
    }
  )();
  return pageChanges;
};

test('a wipe that empties the list puts the view back on a real page', () => {
  // Page 30 was asked for and answered; the answer says there is only one page left.
  assert.deepEqual(runClamp({ requestedPage: 30, currentPage: 30, totalPages: 1 }), [1]);
});

test('a legitimate deep page is left alone', () => {
  assert.deepEqual(runClamp({ requestedPage: 30, currentPage: 30, totalPages: 45 }), []);
});

test('a page is not clamped before its own rows have arrived', () => {
  // A deep link opens on page 30 while the hook still holds its placeholder response, whose total
  // is 1. Clamping on that would throw away the page before it was ever fetched.
  assert.deepEqual(runClamp({ requestedPage: 1, currentPage: 30, totalPages: 1 }), []);
});

test('an error response does not move the page', () => {
  // The server answers a failure with an empty body: page 0, no totals. Nothing was asked and
  // answered, so nothing is clamped.
  assert.deepEqual(runClamp({ requestedPage: 0, currentPage: 30, totalPages: 1 }), []);
});

test('the client-paginated view is not touched by this effect', () => {
  assert.deepEqual(
    runClamp({ serverMode: false, requestedPage: 30, currentPage: 30, totalPages: 1 }),
    []
  );
});

test('the page already in range is left where it is', () => {
  assert.deepEqual(runClamp({ requestedPage: 1, currentPage: 1, totalPages: 1 }), []);
});
