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

const shimmedPath = (bin) => `${bin}${delimiter}${process.env.PATH ?? ''}`;

const executable = (path, contents) => {
  writeFileSync(path, contents);
  chmodSync(path, 0o755);
};

const localLayout = (curlBody) => {
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
  executable(join(bin, 'curl'), curlBody);
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

test('the host-side script forwards the username and pipes the password in', () => {
  const root = mkdtempSync(join(tmpdir(), 'lcm-password-recovery-host-args-'));
  const bin = join(root, 'bin');
  const log = join(root, 'docker.log');
  mkdirSync(bin);
  executable(join(bin, 'docker'), `#!/bin/sh\nprintf '%s\\n' "$*" >> "$DOCKER_LOG"\nexit 0\n`);

  try {
    const run = spawnSync(
      'bash',
      [sourceScript, '--container', 'custom-lcm', '--username', 'owner', '--password-stdin'],
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
      'exec -i custom-lcm /data/scripts/reset-main-admin-password.sh --inside-container --username owner --password-stdin'
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('partial command credentials are refused before recovery starts', () => {
  for (const args of [
    ['--local', '--username', 'owner'],
    ['--local', '--password-stdin']
  ]) {
    const run = spawnSync('bash', [sourceScript, ...args], {
      encoding: 'utf8',
      input: 'NewPassword-2026\n'
    });

    assert.equal(run.status, 2);
    assert.match(
      run.stderr,
      /--username and --password-stdin must be used together\. Omit both to finish in the browser\./
    );
    assert.doesNotMatch(run.stdout, /Waiting for LANCache Manager/);
  }
});

test('local recovery without credentials opens the window and leaves the prompt to the app', () => {
  const { root, script, bin } = localLayout(
    `#!/bin/sh\ncase "$*" in\n  *"/health"*) exit 0 ;;\n  *"/open-main-admin-recovery"*) cat >/dev/null; printf '{"success":true}\\n' ;;\n  *) echo unexpected >&2; exit 1 ;;\nesac\n`
  );

  try {
    const run = spawnSync('bash', [script, '--local', '--url', 'http://127.0.0.1:8080'], {
      encoding: 'utf8',
      env: {
        ...process.env,
        PATH: shimmedPath(bin)
      }
    });

    assert.equal(run.status, 0, run.stderr);
    assert.match(run.stdout, /The recovery window is open for one hour\./);
    assert.match(run.stdout, /Open LANCache Manager in your browser\./);
    assert.doesNotMatch(run.stdout, /Main administrator username/);
    assert.doesNotMatch(run.stdout, /"success":true/);
    assert.doesNotMatch(run.stdout + run.stderr, /key-that-must-not-be-printed/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('local recovery takes the username in the command and the password on stdin', () => {
  const { root, script, bin } = localLayout(
    `#!/bin/sh\ncase "$*" in\n  *"/health"*) exit 0 ;;\n  *"/open-main-admin-recovery"*) cat >/dev/null; printf '{"success":true}\\n' ;;\n  *"/recover-main-admin"*) cat >/dev/null; printf '{"success":true,"message":"Password reset"}\\n' ;;\n  *) echo unexpected >&2; exit 1 ;;\nesac\n`
  );

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
        '--password-stdin'
      ],
      {
        encoding: 'utf8',
        input: 'NewPassword-2026\n',
        env: {
          ...process.env,
          PATH: shimmedPath(bin)
        }
      }
    );

    assert.equal(run.status, 0, run.stderr);
    assert.doesNotMatch(run.stdout, /Open LANCache Manager in your browser/);
    assert.match(run.stdout, /"success":true/);
    assert.match(run.stdout, /Password reset\. Sign in with the new password\./);
    assert.doesNotMatch(run.stdout + run.stderr, /key-that-must-not-be-printed/);
    assert.doesNotMatch(run.stdout + run.stderr, /NewPassword-2026/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('both guides send omitted credentials to the browser', () => {
  for (const guide of [englishGuide, chineseGuide]) {
    assert.match(guide, /\.\/data\/scripts\/reset-main-admin-password\.sh/);
    assert.match(guide, /--username/);
    assert.match(guide, /--password-stdin/);
    assert.match(guide, /--container my-lancache-manager --username admin --password-stdin/);
    assert.match(
      guide,
      /--local --url http:\/\/127\.0\.0\.1:8080 --username admin --password-stdin/
    );
    assert.match(
      guide,
      /docker exec -i lancache-manager \/data\/scripts\/reset-main-admin-password\.sh --username admin --password-stdin/
    );
    assert.doesNotMatch(guide, /--password(?!-stdin)/);
    assert.doesNotMatch(guide, /LCM_API_KEY|LCM_USERNAME|LCM_PASSWORD/);
    assert.doesNotMatch(guide, /never placed in the command line/);
    assert.doesNotMatch(guide, /the script prompts for them/);
  }

  assert.match(englishGuide, /setup screen/);
  assert.match(chineseGuide, /设置屏幕/);
});
