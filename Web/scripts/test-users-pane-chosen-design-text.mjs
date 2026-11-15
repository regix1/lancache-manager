import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { bindLifted, liftConstArrow, translator } from './transpile-module.mjs';

/**
 * The Users page follows the chosen design's wording and placement: times read "12 min ago" and
 * "4 h 59 min left", another device of the same account is a status dot with its device and
 * "city, when", a guest's header names its device and place, an account with no session has one
 * header line, the Sign-in pane has no section help and no service marks, the note about guests
 * already signed in sits behind the prefill help icon, and the labels read "Server time clock" and
 * "Refresh rate".
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const USER_FOLDER = 'src/components/features/user/';
const en = JSON.parse(readWebSource('src/i18n/locales/en.json'));
const t = translator('en');
const MINUTE = 60 * 1000;

const sessionTime = (name) =>
  bindLifted(liftConstArrow(`${USER_FOLDER}sessionTime.ts`, name), {
    parseUtcDate: (value) => new Date(value),
    // The app-wide formatter writes "12m ago"; the Users page must not fall back on it.
    formatRelativeTime: () => '12m ago'
  });

test('Users page times read "12 min ago" and "4 h 59 min left"', () => {
  const formatLastSeen = sessionTime('formatLastSeen');
  const formatTimeLeft = sessionTime('formatTimeLeft');
  const ago = (ms) => new Date(Date.now() - ms).toISOString();
  const ahead = (ms) => new Date(Date.now() + ms).toISOString();

  assert.equal(formatLastSeen(ago(12 * MINUTE + 5000), t), '12 min ago');
  assert.equal(formatLastSeen(ago(61 * MINUTE), t), '1 h ago');
  assert.equal(formatLastSeen(ago(10 * 1000), t), 'just now');
  assert.equal(formatTimeLeft(ahead(299 * MINUTE + 30 * 1000), t), '4 h 59 min left');
  assert.equal(formatTimeLeft(ahead(12 * MINUTE + 30 * 1000), t), '12 min left');
});

test('another device of the same account shows a status dot, its device and "city, when"', () => {
  const source = readWebSource(`${USER_FOLDER}SessionDetail.tsx`);
  const start = source.indexOf("t('user.panes.sections.otherDevices')");
  const section = source.slice(start, source.indexOf('</UserPaneSection>', start));
  assert.ok(start >= 0, 'SessionDetail has no Other devices section');
  assert.match(section, /lead=\{<StatusDot state=\{status\}/, 'the row does not lead with a dot');
  assert.match(
    section,
    /title=\{deviceTitle\(other\.userAgent, t\)\}/,
    'the title is not the device'
  );
  assert.match(section, /t\('user\.panes\.facts\.placeSeen', \{ place: other\.city, when \}\)/);
  assert.equal(
    t('user.panes.facts.placeSeen', { place: 'Oklahoma City', when: '12 min ago' }),
    'Oklahoma City, 12 min ago'
  );
  assert.doesNotMatch(source, /\bMonitor\b/, 'the row still draws a monitor icon');
});

test("a guest's header names its device and place, without the country, and its time left", () => {
  const source = readWebSource(`${USER_FOLDER}SessionDetail.tsx`);
  const guestBranch = source.slice(
    source.indexOf('if (guest) {'),
    source.indexOf('if (account === null)')
  );
  assert.match(guestBranch, /formatLocation\(session\.city, session\.regionName, null\)/);
  assert.equal(
    t('user.panes.header.guestSession', {
      device: 'Chrome on Android in Tulsa, Oklahoma',
      remaining: '4 h 59 min left'
    }),
    'Chrome on Android in Tulsa, Oklahoma, 4 h 59 min left'
  );
});

test('an account with no session has one header line ending with its creation date', () => {
  const source = readWebSource(`${USER_FOLDER}AccountDetail.tsx`);
  assert.match(source, /style: 'dateOnly'/, 'the creation date carries a time of day');
  assert.match(
    source,
    /t\('user\.panes\.list\.created', \{ when: created \}\)\s*\]\.join\(t\('user\.panes\.separator'\)\)/
  );
  assert.match(source, /const lines: React\.ReactNode\[\] = \[summary\];/);
  assert.equal(en.user.panes.list.created, 'created {{when}}');
});

test('the Sign-in pane has no section help and no service marks', () => {
  const source = readWebSource(`${USER_FOLDER}SignInPane.tsx`);
  assert.match(source, /<UserPaneSection title=\{t\('user\.panes\.sections\.howUsersSignIn'\)\}>/);
  assert.doesNotMatch(source, /LoginServiceMark/, 'a service row still draws its brand mark');
});

test('the note about guests already signed in sits behind the prefill help icon', () => {
  const source = readWebSource(`${USER_FOLDER}GuestConfiguration.tsx`);
  const help = source.indexOf("t('user.panes.help.prefillAria')");
  const popover = source.slice(help, source.indexOf('</HelpPopover>', help));
  assert.ok(help >= 0, 'the prefill section has no help icon');
  assert.ok(
    popover.includes("t('user.guest.prefill.existingGuestsNote')"),
    'the note is not in the help'
  );
  assert.equal(
    source.split("t('user.guest.prefill.existingGuestsNote')").length,
    2,
    'the note is also shown'
  );
});

test('the labels read "Server time clock" and "Refresh rate"', () => {
  assert.equal(en.user.panes.facts.clockServer, 'Server time clock');
  assert.equal(en.user.panes.prefs.refreshRate, 'Refresh rate');
  for (const file of ['GuestConfiguration.tsx', 'SessionPreferences.tsx']) {
    const source = readWebSource(`${USER_FOLDER}${file}`);
    assert.doesNotMatch(source, /common\.refreshRate\.title/, `${file} still reads "Refresh Rate"`);
    assert.match(source, /t\('user\.panes\.prefs\.refreshRate'\)/);
  }
});
