import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { getNginxReopenGate } from '../src/utils/nginxReopenAvailability.ts';

const localeMessages = await Promise.all(
  ['en', 'zh'].map(async (locale) => {
    const contents = await readFile(
      new URL(`../src/i18n/locales/${locale}.json`, import.meta.url),
      'utf8'
    );
    return JSON.parse(contents).management.nginxReopen;
  })
);

const datasource = (overrides) => ({
  name: 'default',
  cachePath: '/cache',
  logsPath: '/logs',
  cacheWritable: true,
  logsWritable: true,
  enabled: true,
  layout: 'monolithic',
  nginxReopenAvailable: true,
  ...overrides
});

test('enables destructive actions when nginx reopen is available', () => {
  assert.deepEqual(getNginxReopenGate([datasource({})]), {
    available: true,
    messageKey: null
  });
});

test('disables destructive actions with Docker copy for a monolithic datasource', () => {
  assert.deepEqual(getNginxReopenGate([datasource({ nginxReopenAvailable: false })]), {
    available: false,
    messageKey: 'management.nginxReopen.dockerUnavailable'
  });
});

test('disables destructive actions with bare-metal copy for a bare-metal datasource', () => {
  assert.deepEqual(
    getNginxReopenGate([datasource({ layout: 'bare_metal', nginxReopenAvailable: false })]),
    {
      available: false,
      messageKey: 'management.nginxReopen.bareMetalUnavailable'
    }
  );
});

test('bare-metal copy explains both container and host nginx recovery paths', () => {
  for (const messages of localeMessages) {
    assert.match(messages.bareMetalUnavailable, /Docker/);
    assert.match(messages.bareMetalUnavailable, /pid: host/);
    assert.match(messages.bareMetalUnavailable, /CAP_KILL/);
  }
});

test('uses only the datasources touched by an entity removal', () => {
  const datasources = [
    datasource({ name: 'docker', nginxReopenAvailable: true }),
    datasource({ name: 'host', layout: 'bare_metal', nginxReopenAvailable: false })
  ];

  assert.equal(getNginxReopenGate(datasources, ['docker']).available, true);
  assert.equal(getNginxReopenGate(datasources, ['host']).available, false);
});
