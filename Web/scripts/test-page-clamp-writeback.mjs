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
 * The two user lists count their rows from an array already in memory. The downloads tab does not
 * any more: its page comes from the server, so it waits for the response to echo the page it asked
 * for before it clamps, the same way the retro list does.
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

/**
 * The write-back effect exactly as the component runs it. `echoedPage` is the page the last
 * response came back holding; it defaults to the page being asked for, which is the settled state
 * the two user lists are always in.
 */
const runWriteBack = (
  path,
  { currentPage, totalPages, viewMode = 'normal', echoedPage = currentPage }
) => {
  const pages = [];
  bindLifted(liftHookCallback(path, 'useEffect', 'setCurrentPage(totalPages)'), {
    currentPage,
    totalPages,
    settings: { viewMode },
    serverPage: { currentPage: echoedPage },
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

test('downloads: a page the server has not answered yet is left alone', () => {
  // The total on hand belongs to the previous request until the response echoes the page that was
  // asked for. Clamping before then takes a deep page away before its own rows ever arrive, which
  // is exactly what breaks a link straight to page 30.
  assert.deepEqual(
    runWriteBack(DOWNLOADS_TAB, { currentPage: 30, totalPages: 1, echoedPage: 1 }),
    []
  );
});

/**
 * The downloads tab shows four views out of one page number, and retro fetches its own page under
 * its own grouping. Its page count is therefore a different number from the one computed here, and
 * clamping against this one would pull a legitimate deep retro page back to the start on every
 * render - the exact failure the clamp inside RetroView was written to avoid.
 */
test('downloads: retro is left alone, because this total counts the wrong list', () => {
  assert.deepEqual(
    runWriteBack(DOWNLOADS_TAB, { currentPage: 30, totalPages: 1, viewMode: 'retro' }),
    []
  );
});

test('downloads: leaving retro clamps the page against the list it landed on', () => {
  // Page 30 came from retro; the grouped list this view pages over has four pages, so the number
  // has to come down or the first render shows an empty page.
  assert.deepEqual(
    runWriteBack(DOWNLOADS_TAB, { currentPage: 30, totalPages: 4, viewMode: 'normal' }),
    [4]
  );
});
