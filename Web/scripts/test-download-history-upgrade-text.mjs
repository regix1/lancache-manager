import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import i18next from 'i18next';

/**
 * The download history upgrade's failure names how many rows still wait to merge. The server sends
 * the bare key with `count`, so the sentence has to read right for one row as well as several.
 */

const locale = (name) =>
  JSON.parse(readFileSync(new URL(`../src/i18n/locales/${name}.json`, import.meta.url), 'utf8'));

const translator = i18next.createInstance();
await translator.init({
  lng: 'en',
  resources: { en: { translation: locale('en') }, zh: { translation: locale('zh') } },
  interpolation: { escapeValue: false }
});

const KEY = 'signalr.downloadHistoryUpgrade.failedWithRemaining';

test('the English sentence reads right for one remaining row and for several', () => {
  const en = translator.getFixedT('en');
  for (const count of [1, 2]) {
    assert.equal(
      en(KEY, { errorDetail: 'timeout', count }),
      `Download history upgrade failed: timeout. Rows still waiting to merge: ${count}. It tries again at the next start.`
    );
  }
});

test('the Chinese sentence carries the count for one row and for several', () => {
  const zh = translator.getFixedT('zh');
  for (const count of [1, 2]) {
    const sentence = zh(KEY, { errorDetail: 'timeout', count });
    assert.ok(sentence.includes(`仍有 ${count} 行等待合并`), sentence);
  }
});
