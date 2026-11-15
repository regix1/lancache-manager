import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow } from './transpile-module.mjs';

/**
 * iPhone and iPad user agents contain "like Mac OS X", so the system check must look for them
 * before the Mac check or an iPhone session reads as macOS. The parser is lifted from the shipped file.
 */

const TYPES = 'src/components/features/user/types.ts';

const parseUserAgent = bindLifted(liftConstArrow(TYPES, 'parseUserAgent'), {});

const cases = [
  [
    'iPhone Safari',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1',
    'iOS',
    'Safari on iOS'
  ],
  [
    'iPad Safari',
    'Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1',
    'iOS',
    'Safari on iOS'
  ],
  [
    'Mac Safari',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15',
    'macOS',
    'Safari on macOS'
  ],
  [
    'Android Chrome',
    'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36',
    'Android 14',
    'Chrome on Android 14'
  ],
  [
    'Linux Firefox',
    'Mozilla/5.0 (X11; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0',
    'Linux',
    'Firefox on Linux'
  ]
];

for (const [name, ua, os, title] of cases) {
  test(`${name} reads as ${title}`, () => {
    const parsed = parseUserAgent(ua);
    assert.equal(parsed.os, os);
    assert.equal(parsed.title, title);
  });
}
