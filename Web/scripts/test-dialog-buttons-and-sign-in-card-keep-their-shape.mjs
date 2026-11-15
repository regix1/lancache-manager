import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { LoadingSpinner } from './markup-shape.mjs';
import { builtStyles, compileTree, make, translator, withBrowser } from './transpile-module.mjs';

// Draws two shipped screens with the built stylesheet (run `npm run build` first) and measures what
// a person sees there: the buttons of a confirm dialog that shows a busy line, before and after the
// click, and the sign-in card's header and form in Password mode at phone and desktop widths. Runs
// only with PLAYWRIGHT_PATH set to a playwright-core install whose Chromium is downloaded.

const translate = translator('en');
const icon = ({ className }) =>
  React.createElement('svg', { className, width: 16, height: 16, 'aria-hidden': true });
const Button = make('src/components/ui/Button.tsx', 'Button', {
  React,
  LoadingSpinner,
  useOptionalDirectoryPermissionsContext: () => null
});
// The theme service sets the border colors at run time; without them a 1px rule computes to none.
const THEME_BORDERS =
  ':root { --theme-border-primary: #808080; --theme-border-secondary: #606060; }';
const page = (styles, body) =>
  `<!doctype html><html lang="en"><head><style>${styles}</style><style>${THEME_BORDERS}</style>` +
  `</head><body>${body}</body></html>`;

const CONFIG = 'management.schedules.services.scheduledPrefill.config';
// The two confirm dialogs that pass a busy line, with the labels they pass.
const BUSY_DIALOGS = [
  {
    name: 'delete schedule record',
    confirmLabel: `${CONFIG}.records.delete`,
    busyLabel: `${CONFIG}.records.deleting`
  },
  {
    name: 'clear stored logins',
    confirmLabel: `${CONFIG}.settings.clearLogins.confirmButton`,
    busyLabel: `${CONFIG}.settings.clearLogins.clearing`
  }
];

test(
  'the buttons of a confirm dialog keep their width when its busy line appears',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    // The dialog's panel: a medium dialog is 32rem wide (Modal.tsx `max-w-lg`) with 1.5rem insets.
    const Modal = ({ children }) =>
      React.createElement(
        'div',
        { className: 'w-full max-w-lg' },
        React.createElement('div', { className: 'px-6' }, children)
      );
    const ConfirmationModal = make(
      'src/components/common/ConfirmationModal.tsx',
      'ConfirmationModal',
      {
        React,
        useTranslation: () => ({ t: translate }),
        AlertTriangle: icon,
        Modal,
        Button,
        LoadingSpinner
      }
    );
    const dialog = ({ confirmLabel, busyLabel }, loading) =>
      renderToStaticMarkup(
        React.createElement(
          ConfirmationModal,
          {
            opened: true,
            onClose: () => undefined,
            onConfirm: () => undefined,
            title: 'title',
            confirmLabel: translate(confirmLabel),
            confirmColor: 'red',
            loading,
            busyLabel: translate(busyLabel)
          },
          React.createElement('p', null, 'body')
        )
      );
    const styles = builtStyles();
    const wrong = [];
    await withBrowser(async (browser) => {
      for (const width of [1440, 768]) {
        for (const busyDialog of BUSY_DIALOGS) {
          const measured = {};
          for (const [state, loading] of [
            ['before the click', false],
            ['after the click', true]
          ]) {
            const tab = await browser.newPage({ viewport: { width, height: 900 } });
            await tab.route('**/*', (route) => route.abort());
            await tab.setContent(page(styles, dialog(busyDialog, loading)));
            measured[state] = await tab.evaluate(() => {
              const row = globalThis.document.querySelector('.confirmation-modal__actions');
              return {
                viewport: globalThis.innerWidth,
                row: row.getBoundingClientRect().width,
                buttons: [...row.querySelectorAll(':scope > button')].map(
                  (button) => button.getBoundingClientRect().width
                ),
                status: row.querySelector('[role="status"]')?.innerText.trim() ?? ''
              };
            });
            await tab.close();
          }
          const before = measured['before the click'];
          const after = measured['after the click'];
          const where = `${busyDialog.name} at ${width} px`;
          assert.equal(before.viewport, width, `viewport width for ${where}`);
          assert.equal(before.row, 464, `the actions row of ${where} is not a medium dialog's`);
          assert.deepEqual(
            [before.status, after.status],
            ['', translate(busyDialog.busyLabel)],
            `busy line shown in ${where}`
          );
          assert.equal(after.buttons.length, 2, `both buttons render in ${where}`);
          after.buttons.forEach((buttonWidth, index) => {
            if (Math.abs(buttonWidth - before.buttons[index]) >= 0.5)
              wrong.push(
                `${where}: button ${index + 1} is ${before.buttons[index]} px before the click and ` +
                  `${buttonWidth} px after it`
              );
          });
        }
      }
    });

    assert.deepEqual(
      wrong,
      [],
      `Confirm changes the width of the dialog's buttons the moment its busy line appears: ${wrong.join('; ')}`
    );
  }
);

test(
  'the Password-mode sign-in card keeps its one-column header and form at every width',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    const accountMode = await import(await compileTree('../src/utils/accountMode.ts'));
    const loginService = await import(await compileTree('../src/utils/loginService.ts'));
    const Card = make('src/components/ui/Card.tsx', 'Card', { React });
    const AuthenticateTab = make(
      'src/components/features/auth/AuthenticateTab.tsx',
      'AuthenticateTab',
      {
        React,
        useState: React.useState,
        useRef: React.useRef,
        Trans: () => null,
        useTranslation: () => ({ t: (key) => key }),
        Key: icon,
        Button,
        Card,
        Alert: () => null,
        CredentialFields: () =>
          React.createElement(
            React.Fragment,
            null,
            React.createElement('input', { name: 'username' }),
            React.createElement('input', { name: 'password', type: 'password' })
          ),
        LoginServiceButtons: () => null,
        authService: {},
        requiresApiKey: accountMode.requiresApiKey,
        usesOidc: accountMode.usesOidc,
        signInServices: loginService.signInServices,
        useAuth: () => ({
          refreshAuth: async () => undefined,
          accountMode: 'password',
          oidcDisplayName: null,
          loginServices: []
        }),
        useErrorHandler: () => ({ notifyError: () => undefined }),
        useNotifySuccess: () => ({ notifySuccess: () => undefined }),
        getErrorMessage: (error) => String(error)
      }
    );
    const markup = renderToStaticMarkup(React.createElement(AuthenticateTab));
    const styles = builtStyles();
    const looks = {};
    await withBrowser(async (browser) => {
      for (const width of [390, 768, 1024, 1440]) {
        const tab = await browser.newPage({ viewport: { width, height: 900 } });
        await tab.route('**/*', (route) => route.abort());
        await tab.setContent(page(styles, markup));
        looks[width] = await tab.evaluate(() => {
          const box = (selector) => {
            const style = globalThis.getComputedStyle(globalThis.document.querySelector(selector));
            return {
              padding: [
                style.paddingTop,
                style.paddingRight,
                style.paddingBottom,
                style.paddingLeft
              ],
              bottomRule: style.borderBottomWidth
            };
          };
          return {
            viewport: globalThis.innerWidth,
            card: globalThis.document.querySelector('.auth-upgrade').getBoundingClientRect().width,
            header: box('.auth-upgrade-header'),
            form: box('.auth-upgrade-form').padding
          };
        });
        await tab.close();
      }
    });

    const wrong = [];
    for (const width of [768, 1024, 1440]) {
      assert.equal(looks[width].viewport, width, `viewport width at ${width} px`);
      assert.ok(
        looks[width].card <= 416,
        `the Password-mode card is wider than one column at ${width} px`
      );
      const { header, form } = looks[width];
      if (
        JSON.stringify({ header, form }) !==
        JSON.stringify({ header: looks[390].header, form: looks[390].form })
      )
        wrong.push(
          `at ${width} px the header has padding ${header.padding.join(' ')} and a ${header.bottomRule} ` +
            `rule and the form padding ${form.join(' ')}; at 390 px they are ` +
            `${looks[390].header.padding.join(' ')}, a ${looks[390].header.bottomRule} rule and ` +
            looks[390].form.join(' ')
        );
    }

    assert.deepEqual(
      wrong,
      [],
      `the one-column Password-mode card is drawn with the two-column card's header line and padding: ${wrong.join('; ')}`
    );
  }
);
