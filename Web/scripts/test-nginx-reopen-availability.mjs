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
  nginxReopenRequirement: 'required',
  nginxReopenCheckOnAction: false,
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

test('denies a required native Windows Docker writer with the typed remedy', () => {
  assert.deepEqual(
    getNginxReopenGate([
      datasource({
        nginxReopenAvailable: false,
        nginxReopenRequirement: 'required',
        nginxReopenCheckOnAction: false,
        nginxReopenHint: 'useLinuxManager'
      })
    ]),
    {
      available: false,
      messageKey: 'management.nginxReopen.windowsDockerUnsupported'
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

test('uses the Windows Docker remedy before other unavailable datasource remedies', () => {
  const datasources = [
    datasource({
      name: 'windows-docker',
      nginxReopenAvailable: false,
      nginxReopenHint: 'useLinuxManager'
    }),
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

  assert.deepEqual(getNginxReopenGate(datasources), {
    available: false,
    messageKey: 'management.nginxReopen.windowsDockerUnsupported'
  });
  assert.deepEqual(getNginxReopenGate(datasources.slice(1, 3)), {
    available: false,
    messageKey: 'management.nginxReopen.enablePidHost'
  });
  assert.deepEqual(getNginxReopenGate(datasources.slice(1)), {
    available: false,
    messageKey: 'management.nginxReopen.grantSignalPrivilege'
  });
});

test('uses the writer check reason when an unavailable datasource has no remedy', () => {
  assert.deepEqual(getNginxReopenGate([datasource({ nginxReopenAvailable: false })]), {
    available: false,
    messageKey: 'management.nginxReopen.writerUnknown'
  });
});

test('allows not-required actions without claiming signal availability', () => {
  assert.deepEqual(
    getNginxReopenGate([
      datasource({
        nginxReopenRequirement: 'notRequired',
        nginxReopenAvailable: false
      })
    ]),
    { available: true, messageKey: null }
  );
});

test('allows supported unknown writers through action preflight', () => {
  for (const deployment of ['native', 'container']) {
    assert.deepEqual(
      getNginxReopenGate([
        datasource({
          deployment,
          nginxReopenRequirement: 'unknown',
          nginxReopenAvailable: false,
          nginxReopenCheckOnAction: true
        })
      ]),
      {
        available: true,
        messageKey: 'management.nginxReopen.checkOnAction'
      }
    );
  }
});

test('denies unknown writers when action preflight is unavailable', () => {
  for (const deployment of ['native', 'container']) {
    assert.deepEqual(
      getNginxReopenGate([
        datasource({
          deployment,
          nginxReopenRequirement: 'unknown',
          nginxReopenAvailable: false,
          nginxReopenCheckOnAction: false
        })
      ]),
      {
        available: false,
        messageKey: 'management.nginxReopen.writerUnknown'
      }
    );
  }
});

// Each hint names one remedy, and that remedy must describe its own fix without
// bleeding into the other remedies. The block also holds the alert heading, which is
// not a remedy, so the keys are read through the gate instead of a fixed list.
const remedyRules = {
  useLinuxManager: {
    required: [/Windows/, /Docker/, /Linux/, /static|静态/i],
    forbidden: [/pid: host|CAP_KILL|docker\.sock/i]
  },
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
  assert.equal(
    localeMessages[0].windowsDockerUnsupported,
    'This manager cannot safely change logs held by a Docker writer while running natively on Windows. Run the manager in a Linux environment with the same log mounts, or use a static log source.'
  );
  assert.equal(
    localeMessages[1].windowsDockerUnsupported,
    '此实现不支持原生 Windows Manager 修改由 Docker 写入进程占用的日志。请在挂载相同日志路径的 Linux 环境中运行 Manager，或使用静态日志源。'
  );

  for (const messages of localeMessages) {
    assert.equal(typeof messages.writerUnknown, 'string');
    assert.equal(typeof messages.checkOnAction, 'string');
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
      name: 'windows-docker',
      layout: 'bare_metal',
      nginxReopenAvailable: false,
      nginxReopenHint: 'useLinuxManager'
    })
  ];

  assert.equal(getNginxReopenGate(datasources, ['docker']).available, true);
  assert.deepEqual(getNginxReopenGate(datasources, ['windows-docker']), {
    available: false,
    messageKey: 'management.nginxReopen.windowsDockerUnsupported'
  });
});
