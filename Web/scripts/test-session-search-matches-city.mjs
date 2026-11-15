import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftConstArrow,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * A Users page row names the city the session was seen in ("Chrome on Windows, Lisbon, ..."), so
 * typing that city in the search finds the row, in both shipped languages. The filter callback, the
 * search, the device label and the parser are all lifted from the shipped files, as the device-label
 * search test does.
 */

const SESSIONS = 'src/components/features/user/UserTab.tsx';
const TYPES = 'src/components/features/user/types.ts';
const CITY = 'Lisbon';

const parseUserAgent = bindLifted(liftConstArrow(TYPES, 'parseUserAgent'), {});
const deviceTitle = bindLifted(liftConstArrow(TYPES, 'deviceTitle'), { parseUserAgent });
const formatLocation = bindLifted(liftConstArrow(TYPES, 'formatLocation'), {});
const sessionMatchesSearch = bindLifted(liftConstArrow(SESSIONS, 'sessionMatchesSearch'), {
  parseUserAgent,
  formatLocation,
  cleanIpAddress: (ip) => ip
});

const source = parseSource(SESSIONS);
const filterCallback = findSoleNode(
  source,
  'search filter callback',
  (node) =>
    ts.isArrowFunction(node) &&
    ts.isCallExpression(node.body) &&
    node.body.expression.getText(source) === 'sessionMatchesSearch'
);

const rows = [
  { id: 'lisbon', city: CITY, regionName: 'Lisbon District', countryName: 'Portugal' },
  { id: 'elsewhere', city: 'Porto', regionName: 'Porto District', countryName: 'Portugal' }
].map((row) => ({
  ...row,
  userAgent:
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36',
  username: null,
  ipAddress: '192.168.1.40',
  publicIpAddress: '203.0.113.7'
}));

test('searching the city a row names finds that row', () => {
  const wrong = [];
  for (const locale of ['en', 'zh']) {
    const t = translator(locale);
    const matches = bindLifted(filterCallback.getText(source), {
      sessionMatchesSearch,
      query: CITY.toLowerCase(),
      deviceTitle,
      t
    });
    const found = rows.filter(matches).map((row) => row.id);
    if (JSON.stringify(found) !== JSON.stringify(['lisbon']))
      wrong.push(
        `${locale}: searching "${CITY.toLowerCase()}" finds ${JSON.stringify(found)}, while the row reads "${t('user.panes.list.sessionLine', { device: deviceTitle(rows[0].userAgent, t), place: CITY, when: '' })}"`
      );
  }
  assert.deepEqual(wrong, [], wrong.join('; '));
});
