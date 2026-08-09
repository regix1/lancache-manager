import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { resolveDatasources } from '../src/utils/datasources.ts';
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

test('resolves the legacy flat config as one monolithic datasource without nginx reopen', () => {
  assert.deepEqual(
    resolveDatasources({
      dataSources: [],
      cachePath: '/legacy-cache',
      logsPath: '/legacy-logs',
      cacheWritable: false,
      logsWritable: true
    }),
    [
      {
        name: 'default',
        cachePath: '/legacy-cache',
        logsPath: '/legacy-logs',
        cacheWritable: false,
        logsWritable: true,
        enabled: true,
        layout: 'monolithic',
        nginxReopenAvailable: false
      }
    ]
  );
});

test('preserves a non-empty configured datasource list', () => {
  const configured = [datasource({ name: 'configured' })];
  const resolved = resolveDatasources({
    dataSources: configured,
    cachePath: '/legacy-cache',
    logsPath: '/legacy-logs',
    cacheWritable: false,
    logsWritable: false
  });

  assert.strictEqual(resolved, configured);
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

// Each hint names one remedy, and that remedy must describe its own fix without
// bleeding into the other two. The block also holds the alert heading, which is
// not a remedy, so the keys are read through the gate instead of a fixed list.
const remedyRules = {
  grantSignalPrivilege: {
    required: [/CAP_KILL/],
    forbidden: [/pid: host|docker\.sock/i]
  },
  enablePidHost: {
    required: [/pid: host/, /CAP_KILL/],
    forbidden: [/docker\.sock/i]
  },
  mountDockerSocket: {
    required: [/docker\.sock/i],
    forbidden: [/pid: host|CAP_KILL/]
  }
};

const messagePrefix = 'management.nginxReopen.';

const remedyKeyForHint = (hint) => {
  const { messageKey } = getNginxReopenGate([
    datasource({ nginxReopenAvailable: false, nginxReopenHint: hint })
  ]);
  assert.equal(typeof messageKey, 'string', `hint ${hint} has no remedy`);
  assert.ok(messageKey.startsWith(messagePrefix), `remedy for ${hint} is outside the block`);
  return messageKey.slice(messagePrefix.length);
};

test('locales contain one matching remedy per hint and stay in parity', () => {
  assert.deepEqual(Object.keys(localeMessages[0]).sort(), Object.keys(localeMessages[1]).sort());

  const hints = Object.keys(remedyRules);
  const remedyKeys = hints.map(remedyKeyForHint);
  assert.equal(new Set(remedyKeys).size, hints.length, 'two hints share one remedy');

  for (const messages of localeMessages) {
    hints.forEach((hint, index) => {
      const key = remedyKeys[index];
      const message = messages[key];
      assert.equal(typeof message, 'string', `${key} is missing from a locale`);
      assert.notEqual(message.trim(), '', `${key} is empty in a locale`);
      for (const pattern of remedyRules[hint].required) {
        assert.match(message, pattern);
      }
      for (const pattern of remedyRules[hint].forbidden) {
        assert.doesNotMatch(message, pattern);
      }
    });
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
