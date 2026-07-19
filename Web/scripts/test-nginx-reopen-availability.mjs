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

test('selects the Docker socket hint reported by the backend', () => {
  assert.deepEqual(
    getNginxReopenGate([
      datasource({ nginxReopenAvailable: false, nginxReopenHint: 'mountDockerSocket' })
    ]),
    {
      available: false,
      messageKey: 'management.nginxReopen.dockerUnavailable'
    }
  );
});

test('selects the signal privilege hint reported by the backend', () => {
  assert.deepEqual(
    getNginxReopenGate([
      datasource({ nginxReopenAvailable: false, nginxReopenHint: 'grantSignalPrivilege' })
    ]),
    {
      available: false,
      messageKey: 'management.nginxReopen.grantSignalPrivilege'
    }
  );
});

test('selects the host PID namespace hint reported by the backend', () => {
  assert.deepEqual(
    getNginxReopenGate([
      datasource({ nginxReopenAvailable: false, nginxReopenHint: 'enablePidHost' })
    ]),
    {
      available: false,
      messageKey: 'management.nginxReopen.enablePidHost'
    }
  );
});

test('does not infer a hint from datasource layout', () => {
  assert.deepEqual(
    getNginxReopenGate([
      datasource({
        layout: 'bare_metal',
        nginxReopenAvailable: false,
        nginxReopenHint: 'mountDockerSocket'
      })
    ]),
    {
      available: false,
      messageKey: 'management.nginxReopen.dockerUnavailable'
    }
  );
});

test('uses privilege, host PID, then Docker socket precedence across unavailable datasources', () => {
  const datasources = [
    datasource({
      name: 'docker',
      nginxReopenAvailable: false,
      nginxReopenHint: 'mountDockerSocket'
    }),
    datasource({
      name: 'host',
      nginxReopenAvailable: false,
      nginxReopenHint: 'enablePidHost'
    }),
    datasource({
      name: 'denied',
      nginxReopenAvailable: false,
      nginxReopenHint: 'grantSignalPrivilege'
    })
  ];

  assert.deepEqual(getNginxReopenGate(datasources.slice(0, 2)), {
    available: false,
    messageKey: 'management.nginxReopen.enablePidHost'
  });
  assert.deepEqual(getNginxReopenGate(datasources), {
    available: false,
    messageKey: 'management.nginxReopen.grantSignalPrivilege'
  });
});

test('uses the legacy Docker fallback when an unavailable datasource has no hint', () => {
  assert.deepEqual(getNginxReopenGate([datasource({ nginxReopenAvailable: false })]), {
    available: false,
    messageKey: 'management.nginxReopen.dockerUnavailable'
  });
});

test('locales contain one matching remedy per hint and stay in parity', () => {
  assert.deepEqual(Object.keys(localeMessages[0]).sort(), Object.keys(localeMessages[1]).sort());

  for (const messages of localeMessages) {
    assert.deepEqual(Object.keys(messages).sort(), [
      'dockerUnavailable',
      'enablePidHost',
      'grantSignalPrivilege'
    ]);
    assert.match(messages.grantSignalPrivilege, /CAP_KILL/);
    assert.doesNotMatch(messages.grantSignalPrivilege, /pid: host|docker\.sock/i);
    assert.match(messages.enablePidHost, /pid: host/);
    assert.match(messages.enablePidHost, /CAP_KILL/);
    assert.doesNotMatch(messages.enablePidHost, /docker\.sock/i);
    assert.match(messages.dockerUnavailable, /docker\.sock/i);
    assert.doesNotMatch(messages.dockerUnavailable, /pid: host|CAP_KILL/);
  }
});

test('uses only the datasources touched by an entity removal', () => {
  const datasources = [
    datasource({ name: 'docker', nginxReopenAvailable: true }),
    datasource({
      name: 'host',
      layout: 'bare_metal',
      nginxReopenAvailable: false,
      nginxReopenHint: 'enablePidHost'
    })
  ];

  assert.equal(getNginxReopenGate(datasources, ['docker']).available, true);
  assert.equal(getNginxReopenGate(datasources, ['host']).available, false);
});
