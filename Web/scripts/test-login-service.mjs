import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The named sign-in services reach the screen through one small module: which connections an
 * ordinary visitor may see, which registration fields a kind needs, what the identity service has
 * to be told, and how a callback failure is worded. Each of those is a rule the three sign-in
 * surfaces and the setup dialog all lean on, so they are checked here once, against the module
 * that ships.
 */

const accountModeUrl = await compileToUrl('../src/utils/accountMode.ts');
const { signInServices, needsClientSecret, loginErrorKey, LOGIN_KINDS } = await import(
  await compileToUrl('../src/utils/loginService.ts', { './accountMode': accountModeUrl })
);
const { validateAccountCredentials } = await import(
  await compileToUrl('../src/utils/accountCredentials.ts')
);

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const services = [
  { id: 'google', kind: 'google', displayName: 'Google' },
  { id: 'github', kind: 'github', displayName: 'GitHub' }
];

test('ordinary sign-in offers the tested services only in the single sign-on modes', () => {
  assert.deepEqual(signInServices(services, 'oidc'), services);
  assert.deepEqual(signInServices(services, 'apiKeyOidc'), services);
  for (const dormant of ['password', 'apiKeyPassword', 'unauthenticated']) {
    assert.deepEqual(signInServices(services, dormant), [], `${dormant} shows sign-in buttons`);
  }
  assert.notEqual(signInServices(services, 'oidc'), services, 'the status list is handed out');
});

test('every kind but Apple asks for the client secret its console issued', () => {
  assert.deepEqual(
    LOGIN_KINDS.map((kind) => [kind, needsClientSecret(kind)]),
    [
      ['google', true],
      ['github', true],
      ['microsoft', true],
      ['apple', false],
      ['customOidc', true]
    ]
  );
});

test('a callback failure is worded from the bounded categories and never from the query', () => {
  for (const code of [
    'connection',
    'authentication',
    'identity',
    'expired',
    'state',
    'unavailable'
  ]) {
    assert.equal(loginErrorKey(code), `accessSetup.errors.${code}`);
  }
  for (const code of [null, '', 'signin', '<script>', 'errors.identity', 'CONNECTION']) {
    assert.equal(loginErrorKey(code), 'accessSetup.oidcFailed', `${code} leaked into a key`);
  }
  const en = JSON.parse(readWebSource('src/i18n/locales/en.json'));
  const zh = JSON.parse(readWebSource('src/i18n/locales/zh.json'));
  for (const locale of [en, zh]) {
    assert.deepEqual(Object.keys(locale.accessSetup.errors).sort(), [
      'authentication',
      'connection',
      'expired',
      'identity',
      'state',
      'unavailable'
    ]);
  }
});

test('every sign-in surface draws the service buttons from the same filtered list', () => {
  for (const surface of [
    'src/components/modals/auth/AuthenticationModal.tsx',
    'src/components/features/auth/AuthenticateTab.tsx',
    'src/components/features/management/steam/AuthenticationManager.tsx'
  ]) {
    const source = readWebSource(surface);
    assert.ok(
      source.includes('signInServices(loginServices, accountMode)'),
      `${surface} does not filter the status list`
    );
    assert.ok(source.includes('<LoginServiceButtons'), `${surface} draws its own buttons`);
    assert.ok(
      source.includes('authService.startLogin(service.id, apiKey.trim())'),
      `${surface} does not start the chosen service`
    );
  }
  const buttons = readWebSource('src/components/features/auth/LoginServiceButtons.tsx');
  assert.ok(buttons.includes('login-service-button--${service.kind}'), 'buttons are unbranded');
  assert.ok(!buttons.includes('style={'), 'inline styles are banned');
});

test('the first-admin step and the local-password panel share one set of credential rules', () => {
  assert.deepEqual(validateAccountCredentials('owner', 'Correct-Horse-9', 'Correct-Horse-9'), {
    username: null,
    password: null,
    confirmPassword: null
  });
  assert.equal(
    validateAccountCredentials('', 'x', '').username,
    'initialization.adminAccount.errors.usernameRequired'
  );
  assert.equal(
    validateAccountCredentials('a'.repeat(65), 'x', '').username,
    'initialization.adminAccount.errors.usernameTooLong'
  );
  assert.equal(
    validateAccountCredentials('owner', 'short', 'short').password,
    'initialization.adminAccount.errors.passwordTooShort'
  );
  assert.equal(
    validateAccountCredentials('owner', 'alllowercaseletters', 'alllowercaseletters').password,
    'initialization.adminAccount.errors.passwordCharacterClasses'
  );
  assert.equal(
    validateAccountCredentials('Correct-Horse-9', 'Correct-Horse-9', 'Correct-Horse-9').password,
    'initialization.adminAccount.errors.passwordSameAsUsername'
  );
  assert.equal(
    validateAccountCredentials('owner', 'Correct-Horse-9', 'Correct-Horse-8').confirmPassword,
    'initialization.adminAccount.errors.passwordsDoNotMatch'
  );
  for (const screen of [
    'src/components/initialization/steps/AdminAccountStep.tsx',
    'src/components/initialization/AccessSetup.tsx'
  ]) {
    assert.ok(
      readWebSource(screen).includes('validateAccountCredentials('),
      `${screen} keeps its own copy of the rules`
    );
  }
  assert.ok(
    readWebSource('src/components/initialization/AccessSetup.tsx').includes(
      'authService.setMainAdminPassword(apiKey.trim(), localUsername.trim(), localPassword)'
    ),
    'the local password is not established through the protected route'
  );
});
