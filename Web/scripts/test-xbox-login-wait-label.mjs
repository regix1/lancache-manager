import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * A saved Xbox sign-in that is still finishing holds the next Log in back. The persistent login
 * host reads what it waits for from the login store and hands that key to the Xbox dialog, which
 * shows it in place of the usual connecting line, so the user knows why the code has not come yet.
 */

const en = JSON.parse(
  await readFile(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);
const english = (key) => key.split('.').reduce((node, part) => node[part], en);
globalThis.xboxWaitLabelTest = { translate: english };
// The host file has no React import (the app builds with the automatic JSX runtime), and a data-URL
// module cannot resolve `react/jsx-runtime`, so its classic-JSX output reads React from the global.
globalThis.React = React;

const react = import.meta.resolve('react');
const passChildren = (name) =>
  moduleUrl(
    `import React from '${react}';\n` +
      `export const ${name} = ({ children }) => React.createElement('div', null, children);`
  );

const modalUrl = await compileToUrl(
  '../src/components/modals/auth/XboxAuthModal.tsx',
  {
    react,
    '@components/ui/Modal': passChildren('Modal'),
    '@components/ui/CustomScrollbar': passChildren('CustomScrollbar'),
    '@components/ui/Button': passChildren('Button'),
    '@components/ui/XboxIcon': moduleUrl('export const XboxIcon = () => null;'),
    './LoginSteps': moduleUrl('export const LoginSteps = () => null;'),
    './LoginAttemptStatus': moduleUrl(
      `import React from '${react}';\n` +
        "export const LoginAttemptStatus = ({ label }) => React.createElement('p', null, label);"
    ),
    './authModalCancel': moduleUrl('export const cancelAuthModalLogin = () => undefined;'),
    '@hooks/useCopyFeedback': moduleUrl(
      'export const useCopyFeedback = () => [false, () => undefined];'
    ),
    '@utils/clipboard': moduleUrl('export const copyText = async () => true;'),
    'react-i18next': moduleUrl(
      'export const useTranslation = () => ({ t: globalThis.xboxWaitLabelTest.translate });'
    ),
    '../../../types': moduleUrl('export const getIntegrationReasonKey = (reason) => reason;')
  },
  { jsx: typescript.JsxEmit.React }
);
const { XboxAuthModal } = await import(modalUrl);

// Loading with no device code yet is the dialog's connecting state.
const connecting = {
  loading: true,
  needsDeviceCode: false,
  deviceUserCode: '',
  deviceVerificationUri: '',
  error: null
};
const actions = {
  handleAuthenticate: async () => false,
  resetAuthForm: () => undefined,
  cancelPendingRequest: () => undefined
};
const waitKey = 'prefill.persistent.finishingSavedSignIn';
const connectingKey = 'modals.xboxAuth.connectingSubtitle';

const renderModal = (loginWaitKey) =>
  renderToStaticMarkup(
    React.createElement(XboxAuthModal, {
      opened: true,
      onClose: () => undefined,
      state: { ...connecting, loginWaitKey },
      actions
    })
  );

/** The persistent Xbox host compiled as shipped, its store stub answering `loginWaitKey`. */
const renderHost = async (loginWaitKey) => {
  const { XboxPersistentLogin } = await import(
    await compileToUrl(
      '../src/components/features/management/schedules/scheduled-prefill/login/XboxPersistentLogin.tsx',
      {
        '@components/modals/auth/XboxAuthModal': modalUrl,
        '@hooks/usePersistentXboxAuth': moduleUrl(
          `export const usePersistentXboxAuth = () => ({ state: ${JSON.stringify(connecting)}, ` +
            'actions: {}, startLogin: () => undefined, resumeModal: () => undefined });'
        ),
        './usePersistentLoginHost': moduleUrl(
          'export const usePersistentLoginHost = () => ({ opened: true, retrying: false, error: null });'
        ),
        '../persistentLoginStore': moduleUrl(
          'export const usePersistentLoginStoreState = () => ' +
            `({ loginWaitKey: ${JSON.stringify(loginWaitKey)}, loginDeadline: null });`
        )
      },
      { jsx: typescript.JsxEmit.React }
    )
  );
  return renderToStaticMarkup(
    React.createElement(XboxPersistentLogin, {
      open: true,
      isRunning: true,
      isAuthenticated: false,
      onAuthenticated: () => undefined,
      onDismiss: () => undefined,
      onRetry: () => undefined
    })
  );
};

test('the connecting Xbox dialog shows what the Log in waits for', () => {
  const markup = renderModal(waitKey);
  assert.ok(markup.includes(english(waitKey)));
  assert.ok(!markup.includes(english(connectingKey)));
});

test('with nothing to wait for the connecting Xbox dialog shows the usual line', () => {
  assert.ok(renderModal(null).includes(english(connectingKey)));
});

test('the persistent Xbox host hands the store wait key to the dialog', async () => {
  assert.ok((await renderHost(waitKey)).includes(english(waitKey)));
  const withoutKey = await renderHost(null);
  assert.ok(!withoutKey.includes(english(waitKey)));
  assert.ok(withoutKey.includes(english(connectingKey)));
});
