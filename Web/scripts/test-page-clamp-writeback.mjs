import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

/**
 * Both paged lists clamp the page they RENDER, which stops a shrinking list showing a blank table.
 * What that clamp does not do is move the page the component is holding.
 *
 * Delete the last rows of page 4 and the view correctly falls back to page 1, but `currentPage` is
 * still 4. Add rows back and the view jumps to page 4 on its own, with nobody having touched the
 * pager. Writing the clamp back is what keeps the number and the view in step.
 *
 * Both lists count their rows from an array already in memory, so there is no not-yet-fetched state
 * to guard against here - unlike the server-paged retro list, which has to wait for its own answer.
 */

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const SITES = [
  {
    label: 'active sessions',
    path: 'src/components/features/user/ActiveSessions.tsx'
  },
  {
    label: 'user accounts',
    path: 'src/components/features/user/UserAccounts.tsx'
  },
  {
    label: 'downloads',
    path: DOWNLOADS_TAB
  }
];

/** The write-back effect exactly as the component runs it. */
const runWriteBack = (path, { currentPage, totalPages, viewMode = 'normal' }) => {
  const pages = [];
  bindLifted(liftHookCallback(path, 'useEffect', 'setCurrentPage(totalPages)'), {
    currentPage,
    totalPages,
    settings: { viewMode },
    setCurrentPage: (page) => pages.push(page)
  })();
  return pages;
};

for (const site of SITES) {
  test(`${site.label}: a page past the end is written back to the last real one`, () => {
    assert.deepEqual(runWriteBack(site.path, { currentPage: 4, totalPages: 1 }), [1]);
  });

  test(`${site.label}: a page still in range is left alone`, () => {
    assert.deepEqual(runWriteBack(site.path, { currentPage: 2, totalPages: 4 }), []);
  });

  test(`${site.label}: the last page is not written back to itself`, () => {
    // Equal, not greater - writing here would set state on every render of the last page.
    assert.deepEqual(runWriteBack(site.path, { currentPage: 4, totalPages: 4 }), []);
  });

  test(`${site.label}: an empty list settles on page 1`, () => {
    // Every total is floored at 1, so an empty list is one empty page rather than zero pages.
    assert.deepEqual(runWriteBack(site.path, { currentPage: 1, totalPages: 1 }), []);
  });
}

/**
 * The downloads tab shows four views out of one page number, and only three of them are paginated
 * on the client. Retro asks the server for its page, so the total computed here counts a list that
 * is empty while retro is showing - it reads 1 no matter how many retro pages exist. Clamping
 * against it would pull a legitimate deep retro page back to the start on every render, which is
 * the exact failure the retro clamp in RetroView was written to avoid.
 */
test('downloads: retro is left alone, because this total counts the wrong list', () => {
  assert.deepEqual(
    runWriteBack(DOWNLOADS_TAB, { currentPage: 30, totalPages: 1, viewMode: 'retro' }),
    []
  );
});

test('downloads: leaving retro clamps the page against the client list it landed on', () => {
  // Page 30 came from retro; the client list this view pages over has four pages, so the number has
  // to come down or the first render shows an empty slice.
  assert.deepEqual(
    runWriteBack(DOWNLOADS_TAB, { currentPage: 30, totalPages: 4, viewMode: 'normal' }),
    [4]
  );
});
