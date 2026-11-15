import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const USER_FOLDER = 'src/components/features/user/';
const userTabSource = readWebSource(`${USER_FOLDER}UserTab.tsx`);
const sessionsSource = readWebSource(`${USER_FOLDER}useUserSessions.ts`);
const preferencesSource = readWebSource(`${USER_FOLDER}SessionPreferences.tsx`);
const threadDefaultsSource = readWebSource(`${USER_FOLDER}useGuestThreadDefaults.ts`);
const typeSource = readWebSource(`${USER_FOLDER}types.ts`);
const userCss = readWebSource('src/styles/features/user.css');

test('the browser session contract exposes safe identity and an optional account key', () => {
  assert.match(
    userTabSource,
    /import\s*\{[\s\S]*type Session[\s\S]*\}\s*from '\.\/types'/,
    'UserTab must consume the shared typed Session contract'
  );
  assert.match(typeSource, /\busername\?:\s*string\s*\|\s*null\s*;/);
  assert.match(typeSource, /\baccountDeleted:\s*boolean\s*;/);
  // Guests and shared access have no account; sessions are tied to accounts by this key only.
  assert.match(typeSource, /\baccountId\?:\s*string\s*\|\s*null\s*;/);
});

test('session rows lead with who, then where, then when', () => {
  const identity = userTabSource.indexOf('title={sessionName(session, t)}');
  const where = userTabSource.indexOf('line={sessionLine(', identity);
  assert.ok(identity >= 0 && where > identity, 'the row does not lead with who');
  assert.match(
    userTabSource,
    /t\('user\.panes\.list\.sessionLine', \{ device, place: session\.city, when \}\)/
  );
  const en = JSON.parse(readWebSource('src/i18n/locales/en.json')).user.panes.list.sessionLine;
  const [device, place, when] = ['{{device}}', '{{place}}', '{{when}}'].map((slot) =>
    en.indexOf(slot)
  );
  assert.ok(device >= 0 && place > device && when > place, 'row line order is not where/when');
  assert.match(userTabSource, /session\.username\s*\?\?\s*''/);
});

test('session loads stage every page and reject late identity responses', () => {
  const firstLoad = sessionsSource.indexOf('const first = await ApiService.getSessions');
  const restLoad = sessionsSource.indexOf('await Promise.all', firstLoad);
  const commit = sessionsSource.indexOf('setSessions(loadedSessions)', restLoad);

  assert.ok(
    firstLoad >= 0 && restLoad > firstLoad && commit > restLoad,
    'pages commit before staging'
  );
  assert.match(sessionsSource, /request !== loadRequestRef\.current/);
  assert.match(sessionsSource, /requestedIdentity !== sessionIdentityRef\.current/);
  assert.match(sessionsSource, /useLayoutEffect\(\(\) => \{[\s\S]*loadRequestRef\.current \+= 1/);
  assert.match(sessionsSource, /`shared:\$\{sessionId \?\? 'none'\}`/);
  assert.match(sessionsSource, /sessionsRef\.current = \[\][\s\S]*setSessions\(\[\]\)/);
});

test('a failed sessions load shows one box with the reason, above rows loaded earlier', () => {
  assert.match(
    userTabSource,
    /<ErrorBlock\s+title=\{[\s\S]*?t\('activeSessions\.initialLoadFailed'\)\s*\}\s+message=\{loadError\}\s+retryLabel=\{t\('common\.retry'\)\}/
  );
  assert.match(sessionsSource, /setLoadError\(getErrorMessage\(err\)\)/);
  // With nothing loaded the box replaces the empty state; with rows it sits above them.
  assert.match(
    userTabSource,
    /if \(total === 0\) \{\s*if \(errorBlock !== null\) return errorBlock;/
  );
  assert.match(userTabSource, /\{errorBlock\}\s*\{rows\}/);
  for (const source of [userTabSource, sessionsSource]) {
    assert.doesNotMatch(source, /activeSessions\.initialLoadFailedMessage/);
    assert.doesNotMatch(source, /activeSessions\.retry/);
    assert.doesNotMatch(source, /activeSessions\.refreshFailed/);
  }
  assert.match(userTabSource, /user\.panes\.list\.noMatch'/);
});

test('only the newest thread-limit read writes the limits or the box', () => {
  assert.match(threadDefaultsSource, /const request = \+\+requestRef\.current/);
  assert.equal(
    (threadDefaultsSource.match(/if \(request !== requestRef\.current\) return;/g) ?? []).length,
    2,
    'both the value writes and the error write must be guarded by the request number'
  );
  assert.match(
    preferencesSource,
    /<ErrorBlock\s+title=\{t\('user\.errors\.loadThreadConfig'\)\}\s+message=\{guestThreadDefaults\.error\}/
  );
  assert.doesNotMatch(preferencesSource, /notifyError\(t\('user\.errors\.loadThreadConfig'\)/);
  assert.match(
    threadDefaultsSource,
    /useReconnectRefetch\(isConnected, \(\) => \{\s*if \(requestRef\.current > 0\) void load\(\);\s*\}\)/
  );
});

test('a box inside a confirmation dialog leaves the icon to the dialog title', () => {
  // Every dialog here has a title icon (its own or the default triangle), so the box inside
  // carries none.
  const files = readdirSync(new URL(`../${USER_FOLDER}`, import.meta.url)).filter((file) =>
    file.endsWith('.tsx')
  );
  const withDialogs = files.filter((file) =>
    readWebSource(`${USER_FOLDER}${file}`).includes('<ConfirmationModal')
  );
  assert.ok(withDialogs.length > 0, 'no confirmation dialog on the Users page');
  for (const file of withDialogs) {
    const dialogs = readWebSource(`${USER_FOLDER}${file}`).split('<ConfirmationModal').slice(1);
    for (const dialog of dialogs) {
      const body = dialog.slice(0, dialog.indexOf('</ConfirmationModal>'));
      for (const alert of body.match(/<Alert\b[^>]*>/g) ?? []) {
        assert.ok(alert.includes('icon={null}'), `${file}: ${alert} repeats the dialog icon`);
      }
    }
  }
});

test('session layout keeps long content within the row', () => {
  assert.match(userCss, /\.users-row__body \{[^}]*min-width:\s*0/);
  assert.match(userCss, /\.users-row__title-line \{[^}]*min-width:\s*0/);
  assert.match(userCss, /\.users-row__line \{[^}]*overflow-wrap:\s*anywhere/);
});

test('published session copy exists in both locales', () => {
  const keys = ['deletedAccount', 'sharedAccess', 'initialLoadFailed', 'accountFilter'];

  for (const locale of ['en', 'zh']) {
    const messages = JSON.parse(readWebSource(`src/i18n/locales/${locale}.json`));
    for (const key of keys) {
      assert.equal(typeof messages.activeSessions?.[key], 'string', `${locale} is missing ${key}`);
    }
    for (const key of ['sessionLine', 'sessionLineNoPlace', 'noMatch']) {
      assert.equal(
        typeof messages.user?.panes?.list?.[key],
        'string',
        `${locale} is missing user.panes.list.${key}`
      );
    }
  }
});
