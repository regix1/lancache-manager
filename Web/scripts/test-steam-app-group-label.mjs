import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

/**
 * The title of a Steam group whose depots never resolved to a game.
 *
 * The endpoint names such a group "Steam App 620" in English, and the browser used to print that
 * verbatim, so a Chinese reader got an English row. The row itself says which case it is: it
 * carries the app id and reports that no member resolved a real name. Reading the title text
 * instead would break on any server rewording and would also rename a game that is genuinely
 * called "Steam App 620", which is why both cases are asserted here.
 */

const DOWNLOADS_TAB = 'src/components/features/downloads/DownloadsTab.tsx';

const locales = {
  en: JSON.parse(readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')),
  zh: JSON.parse(readFileSync(new URL('../src/i18n/locales/zh.json', import.meta.url), 'utf8'))
};

/** The i18next lookup the component gets, for one locale: dotted key, then `{{var}}` filled in. */
const translator =
  (locale) =>
  (key, vars = {}) => {
    const text = key.split('.').reduce((node, part) => node?.[part], locales[locale]);
    assert.equal(typeof text, 'string', `${locale}.json has no string at ${key}`);
    return text.replace(/{{(\w+)}}/g, (_, name) => String(vars[name]));
  };

const groupFor = (locale, row) =>
  bindLifted(liftHookCallback(DOWNLOADS_TAB, 'useCallback', 'downloads.tab.groups.steamApp'), {
    t: translator(locale),
    getServiceDisplayName: (service) => service,
    getServiceFilterKey: (service) => service
  })(row, []);

/** A merged Steam game row in the shape the endpoint sends. */
const steamRow = (overrides) => ({
  id: 'game-appid-620',
  service: 'steam',
  appName: 'Steam App 620',
  steamAppId: 620,
  hasRealGameName: false,
  groupType: 'game',
  downloadIds: [11],
  clientIps: ['192.168.1.10'],
  totalBytes: 4096,
  cacheHitBytes: 3072,
  cacheMissBytes: 1024,
  requestCount: 2,
  startTimeUtc: '2026-08-30T10:00:00Z',
  lastStartTimeUtc: '2026-08-30T11:00:00Z',
  isEvicted: false,
  isPartiallyEvicted: false,
  ...overrides
});

test('a Steam group with an app id and no resolved title is named in the local language', () => {
  assert.equal(groupFor('en', steamRow({})).name, 'Steam App 620');
  assert.equal(groupFor('zh', steamRow({})).name, 'Steam 应用 620');
});

// The one that matters: a game actually titled like the placeholder keeps its own title. The row
// says a member resolved that name, so nothing about the text itself is inspected.
test('a resolved game whose title reads like the placeholder keeps that title', () => {
  assert.equal(groupFor('zh', steamRow({ hasRealGameName: true })).name, 'Steam App 620');
});
