import assert from 'node:assert/strict';
import {
  chmodSync,
  copyFileSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync
} from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const repositoryRoot = resolve(import.meta.dirname, '../..');
const sourceScript = join(repositoryRoot, 'scripts/reset-main-admin-password.sh');
const entrypoint = readFileSync(join(repositoryRoot, 'entrypoint.sh'), 'utf8');
const dockerfile = readFileSync(join(repositoryRoot, 'Dockerfile'), 'utf8');
const englishGuide = readFileSync(
  join(repositoryRoot, 'docs-site/content/password-recovery.en.md'),
  'utf8'
);
const chineseGuide = readFileSync(
  join(repositoryRoot, 'docs-site/content/password-recovery.zh.md'),
  'utf8'
);

// The shims must shadow the real docker, curl and jq, but replacing PATH outright loses the
// entries the platform needs to find bash at all, so the shim directory goes in front of the
// inherited one instead.
const shimmedPath = (bin) => `${bin}${delimiter}${process.env.PATH ?? ''}`;

const executable = (path, contents) => {
  writeFileSync(path, contents);
  chmodSync(path, 0o755);
};

const localLayout = () => {
  const root = mkdtempSync(join(tmpdir(), 'lcm-password-recovery-local-'));
  const data = join(root, 'data');
  const scripts = join(data, 'scripts');
  const security = join(data, 'security');
  const bin = join(root, 'bin');
  mkdirSync(scripts, { recursive: true });
  mkdirSync(security);
  mkdirSync(bin);
  const script = join(scripts, 'reset-main-admin-password.sh');
  copyFileSync(sourceScript, script);
  chmodSync(script, 0o755);
  writeFileSync(join(security, 'api_key.txt'), 'key-that-must-not-be-printed\n');
  executable(join(bin, 'jq'), '#!/bin/sh\nprintf "{}\\n"\n');
  executable(
    join(bin, 'curl'),
    `#!/bin/sh\ncase "$*" in\n  *"/health"*) exit 0 ;;\n  *) cat >/dev/null; printf '{"success":true,"message":"Password reset"}\\n' ;;\nesac\n`
  );
  return { root, script, bin };
};

test('the container installs the recovery script in the persistent data folder', () => {
  assert.match(dockerfile, /COPY scripts\/ \/scripts\//);
  assert.match(entrypoint, /\/scripts\/reset-main-admin-password\.sh/);
  assert.match(entrypoint, /\/data\/scripts\/reset-main-admin-password\.sh/);
  assert.match(entrypoint, /install -D -m 0755/);
});

test('the host-side script restarts and enters the selected container', () => {
  const root = mkdtempSync(join(tmpdir(), 'lcm-password-recovery-host-'));
  const bin = join(root, 'bin');
  const log = join(root, 'docker.log');
  mkdirSync(bin);
  executable(join(bin, 'docker'), `#!/bin/sh\nprintf '%s\\n' "$*" >> "$DOCKER_LOG"\nexit 0\n`);

  try {
    const run = spawnSync('bash', [sourceScript, '--container', 'custom-lcm'], {
      encoding: 'utf8',
      env: {
        ...process.env,
        DOCKER_LOG: log,
        PATH: shimmedPath(bin)
      }
    });

    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(readFileSync(log, 'utf8').trim().split('\n'), [
      'inspect custom-lcm',
      'restart custom-lcm',
      'exec -it custom-lcm /data/scripts/reset-main-admin-password.sh --inside-container'
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the host-side script forwards optional username and password', () => {
  const root = mkdtempSync(join(tmpdir(), 'lcm-password-recovery-host-args-'));
  const bin = join(root, 'bin');
  const log = join(root, 'docker.log');
  mkdirSync(bin);
  executable(join(bin, 'docker'), `#!/bin/sh\nprintf '%s\\n' "$*" >> "$DOCKER_LOG"\nexit 0\n`);

  try {
    const run = spawnSync(
      'bash',
      [
        sourceScript,
        '--container',
        'custom-lcm',
        '--username',
        'owner',
        '--password',
        'NewPassword-2026'
      ],
      {
        encoding: 'utf8',
        env: {
          ...process.env,
          DOCKER_LOG: log,
          PATH: shimmedPath(bin)
        }
      }
    );

    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(readFileSync(log, 'utf8').trim().split('\n'), [
      'inspect custom-lcm',
      'restart custom-lcm',
      'exec -it custom-lcm /data/scripts/reset-main-admin-password.sh --inside-container --username owner --password NewPassword-2026'
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('local recovery prompts when username and password are omitted', () => {
  const { root, script, bin } = localLayout();

  try {
    const run = spawnSync('bash', [script, '--local', '--url', 'http://127.0.0.1:8080'], {
      encoding: 'utf8',
      input: 'owner\nNewPassword-2026\nNewPassword-2026\n',
      env: {
        ...process.env,
        PATH: shimmedPath(bin)
      }
    });

    assert.equal(run.status, 0, run.stderr);
    assert.match(
      run.stdout,
      /If you do not enter a username and password in the command, the script will prompt you for them\./
    );
    assert.match(run.stdout, /"success":true/);
    assert.match(run.stdout, /Password reset\. Sign in with the new password\./);
    assert.doesNotMatch(run.stdout + run.stderr, /key-that-must-not-be-printed/);
    assert.doesNotMatch(run.stdout + run.stderr, /NewPassword-2026/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('local recovery accepts username and password in the command', () => {
  const { root, script, bin } = localLayout();

  try {
    const run = spawnSync(
      'bash',
      [
        script,
        '--local',
        '--url',
        'http://127.0.0.1:8080',
        '--username',
        'owner',
        '--password',
        'NewPassword-2026'
      ],
      {
        encoding: 'utf8',
        env: {
          ...process.env,
          PATH: shimmedPath(bin)
        }
      }
    );

    assert.equal(run.status, 0, run.stderr);
    assert.doesNotMatch(run.stdout, /If you do not enter a username and password/);
    assert.match(run.stdout, /"success":true/);
    assert.match(run.stdout, /Password reset\. Sign in with the new password\./);
    assert.doesNotMatch(run.stdout + run.stderr, /key-that-must-not-be-printed/);
    assert.doesNotMatch(run.stdout + run.stderr, /NewPassword-2026/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('both guides lead with the generated script and optional credentials', () => {
  for (const guide of [englishGuide, chineseGuide]) {
    assert.match(guide, /\.\/data\/scripts\/reset-main-admin-password\.sh/);
    assert.match(guide, /--username/);
    assert.match(guide, /--password/);
    assert.doesNotMatch(guide, /LCM_API_KEY|LCM_USERNAME|LCM_PASSWORD/);
    assert.doesNotMatch(guide, /never placed in the command line/);
  }
});
