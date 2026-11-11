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
 * The Users page search matches the device label a row shows. A browser or system the parser does
 * not know reads "Unknown" (or "未知") on the row, so searching for that word finds the row. The
 * filter callback, the search, the label and the parser are all lifted from the shipped files.
 */

const SESSIONS = 'src/components/features/user/ActiveSessions.tsx';
const TYPES = 'src/components/features/user/types.ts';

const parseUserAgent = bindLifted(liftConstArrow(TYPES, 'parseUserAgent'), {});
const sessionMatchesSearch = bindLifted(liftConstArrow(SESSIONS, 'sessionMatchesSearch'), {
  parseUserAgent,
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
  ['bot', 'SomeBot/1.0'],
  ['bot-on-windows', 'SomeBot/1.0 (Windows NT 10.0; Win64; x64)'],
  ['no-agent', null],
  [
    'chrome',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'
  ]
].map(([id, userAgent]) => ({
  id,
  userAgent,
  username: null,
  ipAddress: null,
  publicIpAddress: null
}));

const cases = [
  ['en', 'unknown', ['bot', 'bot-on-windows', 'no-agent'], 'the English Unknown label'],
  ['zh', '未知', ['bot', 'bot-on-windows', 'no-agent'], 'the zh Unknown label'],
  ['en', 'chrome', ['chrome'], 'a known browser by name']
];

for (const [locale, query, expected, label] of cases) {
  test(`searching the ${locale} page finds rows by ${label}`, () => {
    const deviceTitle = bindLifted(liftConstArrow(SESSIONS, 'deviceTitle'), {
      t: translator(locale),
      parseUserAgent
    });
    const matches = bindLifted(filterCallback.getText(source), {
      sessionMatchesSearch,
      searchQuery: query,
      deviceTitle
    });
    assert.deepEqual(
      rows.filter(matches).map((row) => row.id),
      expected
    );
  });
}

test('the label helper is declared before the search filter reads it', () => {
  const [deviceTitleStart, filterStart] = ['deviceTitle', 'filteredActiveSessions'].map((name) =>
    findSoleNode(
      source,
      `${name} declaration`,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
    ).getStart(source)
  );
  assert.ok(deviceTitleStart < filterStart);
});
