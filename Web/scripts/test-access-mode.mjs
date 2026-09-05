import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const { requiresApiKey, usesOidc } = await import(
  await compileToUrl('../src/utils/accountMode.ts')
);
const accountModeUrl = await compileToUrl('../src/utils/accountMode.ts');
const { LOGIN_KINDS, callbackPaths } = await import(
  await compileToUrl('../src/utils/loginService.ts', { './accountMode': accountModeUrl })
);
const { getFocusable } = await import(await compileToUrl('../src/utils/focus.ts'));

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');
const accessSource = readWebSource('src/components/initialization/AccessSetup.tsx');
const accessStyles = readWebSource('src/styles/features/access-setup.css');
const gateSource = readWebSource('src/components/modals/SetupGate.tsx');
const cardSource = readWebSource('src/components/ui/SelectableCard.tsx');
const platformSource = readWebSource('src/components/initialization/steps/PlatformSetupStep.tsx');
const depotSource = readWebSource('src/components/modals/setup/DepotInitializationModal.tsx');

/** The props of the first `<SelectableCard key={name}` element in `source`. */
const cardProps = (source, name) => {
  const open = source.indexOf(`key={${name}}`);
  assert.ok(open > 0, `the ${name} card is missing`);
  const tagStart = source.lastIndexOf('<SelectableCard', open);
  // The element closes on its own line at the same indentation as it opened, so a `/>` inside
  // an icon prop is not mistaken for the end of the card.
  const indent = source.slice(source.lastIndexOf('\n', tagStart) + 1, tagStart);
  const close = source.slice(open).search(new RegExp(`\r?\n${indent}/>`));
  assert.ok(close > 0, `the ${name} card never closes`);
  return source.slice(tagStart, open + close);
};

test('each access mode requires only its selected sign-in credentials', () => {
  for (const [mode, key, oidc] of [
    ['password', false, false],
    ['apiKeyPassword', true, false],
    ['apiKeyOidc', true, true],
    ['oidc', false, true],
    ['unauthenticated', false, false]
  ]) {
    assert.equal(requiresApiKey(mode), key, mode);
    assert.equal(usesOidc(mode), oidc, mode);
  }
});

test('dialog tab stops include only the selected radio in each group', (t) => {
  const previous = globalThis.HTMLInputElement;
  class Input {
    type = 'radio';
    name = 'access';
    form = null;
    offsetParent = {};
    checked = false;
  }
  globalThis.HTMLInputElement = Input;
  t.after(() => {
    if (previous === undefined) delete globalThis.HTMLInputElement;
    else globalThis.HTMLInputElement = previous;
  });

  const first = new Input();
  const selected = new Input();
  selected.checked = true;
  const separate = new Input();
  separate.name = 'other';
  const hidden = { offsetParent: null };
  const button = { offsetParent: {} };
  const dialog = { querySelectorAll: () => [first, selected, separate, hidden, button] };

  assert.deepEqual(getFocusable(dialog), [selected, separate, button]);
  selected.checked = false;
  assert.deepEqual(getFocusable(dialog), [first, separate, button]);
  selected.form = {};
  assert.deepEqual(getFocusable(dialog), [first, selected, separate, button]);
});

test('SSO selection and setup actions require an HTTPS page', () => {
  assert.ok(accessSource.includes("const https = window.location.protocol === 'https:'"));
  assert.ok(accessSource.includes('const modeBlocked = oidc && !https'));
  assert.ok(
    cardProps(accessSource, 'choice').includes('disabled={busy || (usesOidc(choice) && !https)}')
  );
  assert.ok(accessSource.includes('disabled={busy || modeBlocked}'));
  assert.ok(accessSource.includes('if (busy || modeBlocked) return;'));
  assert.ok(accessSource.includes('if (busy || !https) return;'));
  assert.ok(accessSource.includes('disabled={busy || !https || !apiKey.trim()}'));
  assert.ok(accessSource.includes('disabled={busy || modeBlocked || !apiKey.trim()}'));
  assert.ok(!accessSource.includes("group.id === 'sso' && !https"));
  assert.ok(accessSource.includes("t('accessSetup.ssoRequiresHttps')"));
  assert.ok(cardSource.includes('disabled={disabled}'));
  const styles = readWebSource('src/styles/components/cards.css');
  assert.match(
    styles,
    /\.selectable-card:has\(\.selectable-card__radio:disabled\)\s*\{[^}]*opacity:/
  );
});

test('disabled access choices explain their requirements through the shared card tooltip', () => {
  const choice = cardProps(accessSource, 'choice');
  assert.ok(choice.includes('disabledReason={'));
  assert.ok(choice.includes("t('accessSetup.httpsRequired')"));
  assert.ok(cardSource.includes('<Tooltip content={disabledReason}'));
  assert.ok(cardSource.includes('if (!blocked) return card;'));
  assert.ok(cardSource.includes('tabIndex={0}'));
  assert.ok(cardSource.includes('role="group"'));
  assert.ok(cardSource.includes('aria-describedby={reasonId}'));
  const styles = readWebSource('src/styles/components/cards.css');
  assert.ok(styles.includes('.selectable-card__help:focus-visible'));
  const tooltip = readWebSource('src/components/ui/Tooltip.tsx');
  assert.ok(tooltip.includes('(hover: none), (pointer: coarse)'));
  const focus = tooltip.slice(
    tooltip.indexOf('const handleFocus'),
    tooltip.indexOf('const handleBlur')
  );
  assert.ok(focus.includes('closeNow()'));
  assert.ok(tooltip.includes('event.stopPropagation()'));
});

test('setup secrets opt out of autofill while local account fields retain their purpose', async () => {
  const { noAutofill } = await import(await compileToUrl('../src/utils/autofill.ts'));
  assert.equal(noAutofill.autoComplete, 'off');
  assert.equal(noAutofill['data-bwignore'], 'true');
  const credentials = readWebSource('src/components/ui/CredentialFields.tsx');
  assert.ok(credentials.includes('{...noAutofill}'));
  assert.ok(credentials.includes('name="installation-key"'));
  assert.ok(!credentials.includes('autoComplete="new-password"'));
  assert.ok(credentials.includes('autoComplete="username"'));
  assert.ok(credentials.includes('autoComplete="current-password"'));
  const secret = readWebSource('src/components/ui/PasswordField.tsx');
  assert.ok(secret.includes("autoComplete === 'off' ? noAutofill : {}"));
  assert.ok(accessSource.includes('name="oauth-client-secret"'));
  assert.ok(accessSource.includes('name="oauth-private-key"'));
  assert.ok(accessSource.includes('key={kind}'));
  const changeStep = accessSource.slice(
    accessSource.indexOf('const changeStep'),
    accessSource.indexOf('const copyCallback')
  );
  assert.ok(changeStep.indexOf('heading.current?.focus()') < changeStep.indexOf('setStep(next)'));
  for (const path of [
    'src/components/features/auth/AuthenticateTab.tsx',
    'src/components/modals/auth/AuthenticationModal.tsx'
  ]) {
    const source = readWebSource(path);
    assert.ok(source.includes('<form'));
    assert.ok(source.includes('method="post"'));
    assert.ok(source.includes('event.preventDefault()'));
  }
});

test('the access dialog is assembled from the setup wizard pieces', () => {
  for (const piece of [
    '<SetupGate',
    '<StepHeader',
    '<Badge',
    '<Alert',
    '<CollapsibleRegion',
    '<CredentialFields',
    '<PasswordField',
    '<FormField',
    '<SelectableCard'
  ]) {
    assert.ok(accessSource.includes(piece), `${piece} is not used by the access dialog`);
  }
  // The step badge and progress strip are drawn once, by the gate, for both wizards.
  assert.ok(accessSource.includes('steps={{ current: stepNumber, total: totalSteps'));
  assert.ok(depotSource.includes('steps={{ current: stepInfo.number, total: stepInfo.total'));
  assert.ok(!accessSource.includes('<ProgressBar') && !depotSource.includes('<ProgressBar'));
  for (const piece of ['<Badge', '<ProgressBar', 'steps.label']) {
    assert.ok(gateSource.includes(piece), `${piece} is not drawn by the gate`);
  }
  for (const source of [accessSource, accessStyles]) {
    assert.ok(!source.includes('--theme-accent'), 'selection is drawn in the accent colour');
    assert.ok(!source.includes('color-mix('), 'color-mix() is banned');
  }
  assert.ok(!accessSource.includes('style={'), 'inline styles are banned');
});

test('every access option renders the same lines whether or not it is selected', () => {
  const label = cardProps(accessSource, 'choice');
  assert.ok(
    !label.includes('mode === choice &&'),
    'a line is mounted only for the selected option, so every row below it moves on selection'
  );
  for (const line of ['title', 'description', 'warning']) {
    assert.ok(
      label.includes(`accessSetup.modes.\${choice}.${line}`),
      `option ${line} is not always rendered`
    );
  }
  assert.ok(cardSource.includes('type="radio"'), 'options are no longer native radios');
  assert.ok(cardSource.includes('<label'), 'the card is no longer the label of its radio');
  assert.ok(label.includes('name="account-mode"'), 'radios do not share one group');
});

test('every sign-in service card renders the same lines whether or not it is selected', () => {
  const label = cardProps(accessSource, 'candidate');
  assert.ok(
    !label.includes('kind === candidate &&'),
    'a line is mounted only for the selected service, so the cards move on selection'
  );
  assert.ok(label.includes('<LoginServiceMark kind={candidate}'), 'the service mark is missing');
  for (const line of ['title', 'note']) {
    assert.ok(
      label.includes(`services.\${candidate}.${line}`) || label.includes('serviceName(candidate)'),
      `service ${line} is not always rendered`
    );
  }
  assert.ok(label.includes('name="login-kind"'), 'service radios do not share one group');
  assert.ok(label.includes('layout="stack"'), 'service cards are not the centred variant');
  assert.deepEqual(LOGIN_KINDS, ['google', 'github', 'microsoft', 'apple', 'customOidc']);
});

test('the stacked card modifier is written out so the stylesheet keeps its centring rule', () => {
  const cardStyles = readWebSource('src/styles/components/cards.css');
  const stackRule = cardStyles.match(/\.selectable-card--stack\s*\{([^}]*)\}/);
  assert.ok(stackRule, 'the stacked card rule is missing');
  for (const declaration of ['flex-direction: column', 'text-align: center', 'padding-top']) {
    assert.ok(stackRule[1].includes(declaration), `stacked cards lost ${declaration}`);
  }
  // A layered class the source never spells out is dropped from the built stylesheet, so the
  // modifier must appear as one literal token rather than be assembled from the layout value.
  assert.ok(cardSource.includes("selectable-card--stack'"), 'the stack modifier is interpolated');
  assert.ok(
    cardSource.includes("selectable-card--checked'"),
    'the checked modifier is interpolated'
  );
  assert.ok(!cardSource.includes('selectable-card--${'), 'a card modifier is built at runtime');
});

test('the platform, Steam sign-in and owner-service choices are the same card as the access options', () => {
  const steamSource = readWebSource('src/components/initialization/steps/SteamPicsAuthStep.tsx');
  for (const [source, group] of [
    [platformSource, 'name="platform"'],
    [steamSource, 'name="steam-auth-mode"'],
    [accessSource, 'name="owner-service"']
  ]) {
    assert.ok(source.includes('<SelectableCard'), `${group} is not a SelectableCard`);
    assert.ok(source.includes(group), `${group} radios do not share one group`);
    assert.ok(
      !/border-\[var\(--theme-primary\)\]/.test(source),
      `${group} keeps its own card recipe`
    );
  }
  assert.ok(
    platformSource.includes('onDeselect={() => onSelect(null)}'),
    'a platform can no longer be unchosen'
  );
  assert.ok(cardSource.includes('onClick={onDeselect && checked ? onDeselect : undefined}'));
  for (const source of [accessSource, platformSource, steamSource]) {
    assert.ok(!source.includes('choiceClassName') && !source.includes('getCardClassName'));
  }
});

test('setup fields and action rows share one recipe across the access dialog and the wizard steps', () => {
  const forms = readWebSource('src/styles/components/forms.css');
  assert.ok(forms.includes('.setup-input {') && forms.includes('.setup-actions {'));
  assert.ok(forms.includes('min-height: 44px'), 'the phone touch floor is missing');
  for (const step of [
    'AdminAccountStep',
    'DatabaseSetupStep',
    'ExternalDatabaseSetupStep',
    'EpicAuthStep',
    'SteamApiKeyStep'
  ]) {
    const source = readWebSource(`src/components/initialization/steps/${step}.tsx`);
    assert.ok(source.includes('themed-input setup-input'), `${step} does not use the shared field`);
    assert.ok(!source.includes('px-3 py-2'), `${step} keeps a private field recipe`);
  }
  assert.equal(accessSource.includes("const inputClassName = 'themed-input setup-input';"), true);
  assert.ok(accessSource.includes('className="setup-actions setup-actions--split"'));
  for (const step of [
    'PlatformSetupStep',
    'SteamApiKeyStep',
    'EpicAuthStep',
    'PermissionsCheckStep'
  ]) {
    const source = readWebSource(`src/components/initialization/steps/${step}.tsx`);
    assert.ok(
      source.includes('className="setup-actions'),
      `${step} does not use the shared action row`
    );
  }
  assert.ok(
    !accessStyles.includes('access-setup-actions') && !accessStyles.includes('access-choice')
  );
});

test('the flow only grows to three steps once new credentials are committed', () => {
  const match = accessSource.match(/const showsTestStep =([\s\S]*?);\r?\n/);
  assert.ok(match, 'showsTestStep is not declared');
  const showsTestStep = new Function(
    'step',
    'credentialsNeeded',
    'pendingTest',
    `return ${match[1].trim()};`
  );
  assert.equal(showsTestStep('choose', true, false), false, 'comparing choices resized the flow');
  assert.equal(showsTestStep('choose', false, false), false);
  assert.equal(showsTestStep('configure', true, false), true);
  assert.equal(
    showsTestStep('configure', false, false),
    false,
    'a connection tested earlier needs no test step'
  );
  assert.equal(showsTestStep('test', false, true), true);
  assert.equal(showsTestStep('choose', false, true), true, 'a pending test hides its own step');
  assert.ok(
    accessSource.includes('const totalSteps = showsTestStep ? 3 : 2;'),
    'the step count no longer follows showsTestStep'
  );
});

test('both callback URLs of the chosen service are shown read-only with a copy action', () => {
  for (const kind of ['google', 'github', 'microsoft', 'apple']) {
    assert.deepEqual(callbackPaths(kind), {
      callback: `/api/auth/login/${kind}/callback`,
      setupCallback: `/api/auth/login/${kind}/setup-callback`
    });
  }
  assert.deepEqual(callbackPaths('customOidc'), {
    callback: '/api/auth/oidc/callback',
    setupCallback: '/api/auth/oidc/setup-callback'
  });
  assert.ok(accessSource.includes('const paths = callbackPaths(kind);'));
  assert.ok(accessSource.includes('`${getApiUrl()}${paths.callback}`'));
  assert.ok(accessSource.includes('`${getApiUrl()}${paths.setupCallback}`'));
  assert.deepEqual(
    [...accessSource.matchAll(/renderCallback\('(\w+)', (\w+)\)/g)].map((found) => found.slice(1)),
    [
      ['callback', 'callback'],
      ['setupCallback', 'setupCallback']
    ]
  );
  assert.ok(
    accessSource.includes("from '@utils/clipboard'"),
    'copy does not use the shared helper'
  );
});

test('testing a connection stages the settings and then starts the real sign-in', () => {
  const saveStart = accessSource.indexOf('const save = async () => {');
  const save = accessSource.slice(saveStart, accessSource.indexOf('const testSignIn', saveStart));
  const staged = save.indexOf('authService.configureAccess({');
  const started = save.indexOf('await startTest(');
  assert.ok(staged > 0 && started > staged, 'the test does not follow the staged save');
  assert.ok(
    save.includes('credentialsNeeded || result.requiresLoginTest || result.requiresOidcTest'),
    'a mode change that keeps tested connections is sent through the test step'
  );
  assert.ok(
    accessSource.includes('authService.startLogin(loginId, apiKey.trim(), true)'),
    'the test does not start the pending connection'
  );
  assert.ok(
    !accessSource.includes("'accessSetup.testConnection'") ||
      accessSource.includes('type="submit"\n                        form="access-setup-form"'),
    'the Test connection button does not submit the configure form'
  );
});

test('callback guidance shows address requirements before the URLs and keeps testing details expandable', () => {
  const en = JSON.parse(readWebSource('src/i18n/locales/en.json')).accessSetup;
  const zh = JSON.parse(readWebSource('src/i18n/locales/zh.json')).accessSetup;
  assert.ok(accessSource.includes("t('accessSetup.callbackOrigin')"));
  assert.ok(accessSource.includes("t('accessSetup.localAddress')"));
  assert.ok(accessSource.includes("t('accessSetup.addressHelp')"));
  assert.ok(accessSource.includes('<p>{t(`accessSetup.services.${kind}.addresses`)}</p>'));
  assert.ok(accessSource.includes("<p>{t('accessSetup.privateHosting')}</p>"));
  assert.ok(accessSource.includes('t(`accessSetup.services.${kind}.testing`)'));
  assert.ok(
    accessSource.indexOf('t(`accessSetup.services.${kind}.addresses`)') <
      accessSource.indexOf("renderCallback('callback', callback)")
  );
  assert.ok(accessSource.includes('const [addressHelp, setAddressHelp] = useState(false)'));
  assert.ok(accessSource.includes('!oidc && !window.isSecureContext'));
  assert.ok(!accessSource.includes('hint={t(`accessSetup.${target}Hint`)}'));
  for (const [locale, text] of [
    ['en', en],
    ['zh', zh]
  ]) {
    assert.equal(typeof text.callbackOrigin, 'string', `${locale} has no URL explanation`);
    assert.equal(typeof text.localAddress, 'string', `${locale} has no loopback explanation`);
    assert.equal(typeof text.addressHelp, 'string', `${locale} has no address disclosure`);
    for (const kind of LOGIN_KINDS) {
      assert.equal(typeof text.services[kind].addresses, 'string', `${locale} ${kind} addresses`);
      assert.equal(typeof text.services[kind].testing, 'string', `${locale} ${kind} testing`);
      assert.ok(/HTTPS/.test(text.services[kind].addresses), `${locale} ${kind} HTTPS requirement`);
    }
    for (const key of ['callbackOrigin', 'httpsWarning']) {
      assert.ok(!/public|公开|公网/.test(text[key]), `${locale} ${key} demands a public address`);
    }
    assert.ok(!/public|公开|公网/.test(text.errors.state), `${locale} state error demands public`);
    assert.ok(!/public|公开|公网/.test(text.oidcFailed), `${locale} oidcFailed demands public`);
    assert.ok(/HTTP/.test(text.httpsWarning), `${locale} has no local HTTP guidance`);
    for (const mode of ['password', 'apiKeyPassword', 'apiKeyOidc', 'oidc']) {
      assert.ok(!/MFA|multi-factor|多因素/i.test(text.modes[mode].warning));
    }
    assert.ok(!/MFA|multi-factor|多因素/i.test(text.oidcSafety));
    assert.ok(/IP/.test(text.services.google.addresses), `${locale} google IP restriction`);
    assert.ok(/127\.0\.0\.1/.test(text.services.google.testing), `${locale} google loopback`);
    assert.ok(/localhost/.test(text.services.google.testing), `${locale} google localhost`);
    assert.ok(/127\.0\.0\.1/.test(text.services.github.testing), `${locale} github loopback`);
    assert.ok(
      /wildcard|通配符/.test(text.services.github.register),
      `${locale} github exact callbacks`
    );
    assert.ok(/127\.0\.0\.1/.test(text.services.microsoft.testing), `${locale} microsoft loopback`);
    assert.ok(/::1/.test(text.services.microsoft.testing), `${locale} microsoft IPv6 restriction`);
    assert.match(text.ssoRequiresHttps, /HTTPS/);
    assert.match(text.localAddress, /HTTPS/);
    for (const kind of LOGIN_KINDS) {
      assert.doesNotMatch(text.services[kind].testing, /http:\/\//);
    }
    assert.ok(/localhost/.test(text.services.apple.addresses), `${locale} apple localhost`);
    assert.ok(/HTTPS/.test(text.services.customOidc.testing), `${locale} issuer HTTPS requirement`);
    assert.ok(/HTTPS/.test(text.issuerHint), `${locale} issuer field HTTPS requirement`);
    assert.equal(text.services.apple.deployment, undefined);
    assert.equal(text.callbacksIntro, undefined);
    assert.equal(text.callbackHint, undefined);
    assert.equal(text.setupCallbackHint, undefined);
  }
  for (const kind of LOGIN_KINDS) {
    assert.ok(
      en.services[kind].register.split(/\s+/).length <= 35,
      `${kind} registration is too long`
    );
    assert.ok(
      en.services[kind].addresses.split(/\s+/).length <= 25,
      `${kind} address requirement is too long`
    );
  }
  assert.match(en.services.github.addresses, /^This app requires HTTPS/);
  assert.match(en.services.customOidc.addresses, /^This app requires HTTPS/);
});

test('access setup separates sign-in requirements, safety notes and identity format guidance', () => {
  assert.ok(
    accessSource.includes("description={t('accessSetup.testExplanation', { name: pendingName })}")
  );
  assert.ok(!accessSource.includes("t('accessSetup.descriptions.test')"));
  assert.ok(
    !accessSource.includes(
      '<Alert color="info">{t(\'accessSetup.testExplanation\', { name: pendingName })}</Alert>'
    )
  );
  for (const locale of ['en', 'zh']) {
    const text = JSON.parse(readWebSource(`src/i18n/locales/${locale}.json`)).accessSetup;
    assert.equal(text.descriptions.test, undefined);
    assert.match(text.advancedHint, /primary administrator|主管理员/);
    assert.doesNotMatch(text.advancedHint, /^Optional|^可选/);
    assert.match(text.modes.apiKeyOidc.description, /approved SSO account|获准的单点登录账户/);
    assert.match(text.modes.apiKeyOidc.warning, /shared|共用/);
    assert.doesNotMatch(text.modes.apiKeyOidc.warning, /requires|account|要求|账户/i);
    assert.match(text.keyOwnership, /Every mode|所有模式/);
    assert.match(text.keyOwnership, /first account|第一个账户/);
    assert.match(text.keyOwnership, /ownership and recovery|所有权和恢复/);
    assert.match(text.testExplanation, /failed test.*unchanged|测试失败不会更改/);
    for (const kind of LOGIN_KINDS) {
      assert.doesNotMatch(text.services[kind].subjects, /primary administrator|主管理员/);
      assert.doesNotMatch(text.services[kind].subjects, /^Optional|^可选/);
      assert.match(text.services[kind].subjects, /one .* per line|one per line|每行一个/);
      assert.match(text.services[kind].subjects, /email addresses|电子邮件地址/);
    }
    for (const kind of ['google', 'github', 'apple']) {
      assert.doesNotMatch(text.services[kind].testing, /HTTPS|LAN|VPN|局域网|公网/);
    }
    assert.match(text.services.google.addresses, /registered domain|已注册的域名/);
    assert.match(text.services.microsoft.subjects, /GUID:.*GUID/);
    assert.match(text.services.github.subjects, /Numeric|数字/);
    assert.match(text.services.customOidc.subjects, /sub/);
  }
});

test('the sign-in result markers are read once and stripped while accessSetup stays', () => {
  assert.ok(
    accessSource.includes("params.get('loginTest') === 'success' ? params.get('loginId') : null"),
    'a test counts as successful without the success marker'
  );
  assert.ok(
    accessSource.includes("for (const marker of ['oidcError', 'loginTest', 'loginId'])"),
    'the result markers are left in the address bar'
  );
  assert.ok(
    !accessSource.includes("searchParams.delete('accessSetup')"),
    'the dialog strips the marker that opened it'
  );
  assert.ok(
    accessSource.includes('const testConfirmed = testedService !== undefined && !pendingTest;'),
    'the tested state is not confirmed against the server status'
  );
  assert.ok(
    accessSource.includes('loginServices.find((service) => service.id === testedId)'),
    'the tested connection is not looked up in the server status'
  );
  assert.ok(
    accessSource.includes('t(loginErrorKey(errorCode))'),
    'a failure code is not translated through the bounded table'
  );
});

test('the access dialog keeps every secret out of browser storage', () => {
  assert.ok(!/localStorage|sessionStorage/.test(accessSource));
  assert.ok(accessSource.includes("setClientSecret('')"), 'the client secret is kept after saving');
  assert.ok(accessSource.includes("setPrivateKey('')"), 'the Apple key is kept after saving');
  assert.ok(accessSource.includes("setLocalPassword('')"), 'the new local password is kept');
  assert.ok(accessSource.includes("setPassword('')"), 'the owner password is kept after use');
});
