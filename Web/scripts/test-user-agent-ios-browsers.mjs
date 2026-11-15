import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow } from './transpile-module.mjs';

/**
 * Every browser on an iPhone or iPad runs on Safari's engine, so its agent ends in "Safari/" and never
 * says "Chrome/" or "Firefox/": Chrome names itself "CriOS/" and Firefox "FxiOS/". The Users page
 * title has to name the browser the person used. The parser is lifted from the shipped file.
 */

const parseUserAgent = bindLifted(
  liftConstArrow('src/components/features/user/types.ts', 'parseUserAgent'),
  {}
);

const cases = [
  [
    'iPhone Chrome',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1',
    'Chrome on iOS'
  ],
  [
    'iPad Chrome',
    'Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1',
    'Chrome on iOS'
  ],
  [
    'iPhone Firefox',
    'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/125.0 Mobile/15E148 Safari/605.1.15',
    'Firefox on iOS'
  ],
  [
    'iPad Firefox',
    'Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/125.0 Mobile/15E148 Safari/605.1.15',
    'Firefox on iOS'
  ]
];

for (const [name, userAgent, title] of cases) {
  test(`${name} reads as ${title}`, () => {
    assert.equal(
      parseUserAgent(userAgent).title,
      title,
      `${name} is shown under the wrong browser`
    );
  });
}
