// The server sends some times without a zone; they are UTC. Run in a zone that is not UTC so a
// time read as local lands hours away from the right answer.
process.env.TZ = 'America/New_York';

import assert from 'node:assert/strict';
import test from 'node:test';
import { compileTree, moduleUrl, translator } from './transpile-module.mjs';

/**
 * `formatRelativeTime` reads server time strings for "Last seen", "Fetched ... ago" and the other
 * relative times. A string with no `Z` and no offset is UTC, so it has to read the same in every
 * browser zone.
 */

globalThis.relativeTimeTranslate = translator('en');

const { formatRelativeTime } = await import(
  await compileTree('../src/utils/formatters.ts', {
    '@/i18n': moduleUrl(
      `export default { t: (key, vars) => globalThis.relativeTimeTranslate(key, vars) };`
    )
  })
);

/** One hour before now, as an ISO string in UTC without the trailing `Z`. */
const oneHourAgoWithoutZone = () => new Date(Date.now() - 3_600_000).toISOString().slice(0, -1);

test('a server time without a zone reads as UTC', () => {
  assert.equal(formatRelativeTime(oneHourAgoWithoutZone()), '1h ago');
});

test('a server time with a Z reads the same', () => {
  assert.equal(formatRelativeTime(`${oneHourAgoWithoutZone()}Z`), '1h ago');
});

test('a time under a minute old reads in lowercase so it fits inside a sentence', () => {
  assert.equal(formatRelativeTime(new Date()), 'just now');
});
