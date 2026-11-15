import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { builtStyles, make, translator, withBrowser } from './transpile-module.mjs';

// A confirm dialog that names what it waits on (`busyLabel`) shows that line at the start of its
// button row once Confirm is clicked. The two buttons must keep the width and place they had before
// the click, at desktop and phone width, in English and Chinese, for both dialogs that pass a busy
// line, and the status region a screen reader watches must already be there, empty, before the
// click. The shipped ConfirmationModal is rendered with the built stylesheet inside a stand-in for
// the medium dialog: a 32rem panel with the dialog body's inset (1rem, 1.5rem from 640 px), inside
// the page gutter. Run after `npm run build`, with PLAYWRIGHT_PATH set to a playwright-core install
// whose Chromium is downloaded.

const scheduleKey = 'management.schedules.services.scheduledPrefill.config';
const dialogs = [
  {
    name: 'delete schedule record',
    confirm: `${scheduleKey}.records.delete`,
    busy: `${scheduleKey}.records.deleting`
  },
  {
    name: 'clear stored logins',
    confirm: `${scheduleKey}.settings.clearLogins.confirmButton`,
    busy: `${scheduleKey}.settings.clearLogins.clearing`
  }
];

const render = (language, dialog, loading) => {
  const t = translator(language);
  const icon = ({ className }) =>
    React.createElement('svg', { className, width: 16, height: 16, 'aria-hidden': true });
  const LoadingSpinner = make('src/components/common/LoadingSpinner.tsx', 'LoadingSpinner', {
    React,
    Loader2: icon
  });
  const Button = make('src/components/ui/Button.tsx', 'Button', {
    React,
    LoadingSpinner,
    useOptionalDirectoryPermissionsContext: () => null
  });
  const Modal = ({ opened, children }) =>
    opened
      ? React.createElement(
          'div',
          { className: 'px-4' },
          React.createElement(
            'div',
            { className: 'themed-card border max-w-lg w-full', role: 'dialog' },
            React.createElement('div', { className: 'p-4 sm:p-6' }, children)
          )
        )
      : null;
  const ConfirmationModal = make(
    'src/components/common/ConfirmationModal.tsx',
    'ConfirmationModal',
    {
      React,
      useTranslation: () => ({ t }),
      Modal,
      Button,
      LoadingSpinner,
      AlertTriangle: icon
    }
  );
  return renderToStaticMarkup(
    React.createElement(
      ConfirmationModal,
      {
        opened: true,
        onClose: () => undefined,
        onConfirm: () => undefined,
        title: 'title',
        confirmLabel: t(dialog.confirm),
        confirmColor: 'red',
        loading,
        busyLabel: dialog.busy && t(dialog.busy)
      },
      React.createElement('p', null, 'body')
    )
  );
};

test(
  'a confirm dialog with a busy line keeps its buttons in place when Confirm is clicked',
  { skip: !process.env.PLAYWRIGHT_PATH },
  async () => {
    const styles = builtStyles();
    await withBrowser(async (browser) => {
      const page = await browser.newPage();
      await page.route('**/*', (route) => route.abort());
      // What a screen reader is handed: the text under each status region in Chromium's own
      // accessibility tree.
      const accessibility = await page.context().newCDPSession(page);
      await accessibility.send('Accessibility.enable');
      const readStatusRegions = async () => {
        const { nodes } = await accessibility.send('Accessibility.getFullAXTree');
        const byId = new Map(nodes.map((node) => [node.nodeId, node]));
        const textUnder = (node) =>
          (node.childIds ?? [])
            .map((id) => byId.get(id))
            .filter((child) => child !== undefined && !child.ignored)
            .flatMap((child) =>
              child.role?.value === 'StaticText' ? [child.name.value] : textUnder(child)
            );
        return nodes
          .filter((node) => node.role?.value === 'status' && !node.ignored)
          .map((node) => textUnder(node).join(''));
      };
      const read = async (markup) => {
        await page.setContent(
          `<!doctype html><html><head><style>${styles}</style></head><body>${markup}</body></html>`
        );
        // Where each footer button sits and what the busy line shows.
        const footer = await page.evaluate(() => {
          const document = globalThis.document;
          const status = document.querySelector('.confirmation-modal__status');
          return {
            width: globalThis.innerWidth,
            buttons: [...document.querySelectorAll('.confirmation-modal__actions > button')].map(
              (button) => {
                const rect = button.getBoundingClientRect();
                return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
              }
            ),
            status: status && status.innerText.trim()
          };
        });
        return { ...footer, announced: await readStatusRegions() };
      };
      const problems = [];
      for (const width of [1440, 390]) {
        await page.setViewportSize({ width, height: 900 });
        for (const language of ['en', 'zh']) {
          const t = translator(language);
          for (const dialog of dialogs) {
            const where = `${dialog.name}, ${language}, ${width} px`;
            const idle = await read(render(language, dialog, false));
            const busy = await read(render(language, dialog, true));
            assert.equal(idle.width, width, `viewport width for ${where}`);
            assert.equal(idle.buttons.length, 2, `${where}: two buttons idle`);
            assert.equal(busy.buttons.length, 2, `${where}: two buttons busy`);
            if (idle.status) problems.push(`${where}: "${idle.status}" is seen before the click`);
            if (busy.status !== t(dialog.busy)) {
              problems.push(`${where}: the busy line reads "${busy.status}" after the click`);
            }
            // The status region is in the accessibility tree before the click with nothing to
            // read, so the click changes the words inside a region already there.
            if (JSON.stringify(idle.announced) !== JSON.stringify([''])) {
              problems.push(
                `${where}: before the click the status regions read ${JSON.stringify(idle.announced)}`
              );
            }
            if (JSON.stringify(busy.announced) !== JSON.stringify([t(dialog.busy)])) {
              problems.push(
                `${where}: after the click the status regions read ${JSON.stringify(busy.announced)}`
              );
            }
            busy.buttons.forEach((after, index) => {
              const before = idle.buttons[index];
              const moved = ['x', 'y', 'width', 'height'].filter(
                (side) => Math.abs(after[side] - before[side]) > 0.5
              );
              if (moved.length > 0) {
                problems.push(
                  `${where}: button ${index + 1} goes from ${JSON.stringify(before)} to ` +
                    `${JSON.stringify(after)} when Confirm is clicked`
                );
              }
            });
            if (width >= 640 && Math.abs(idle.buttons[0].width - idle.buttons[1].width) > 0.5) {
              problems.push(
                `${where}: the buttons are ${idle.buttons[0].width} and ${idle.buttons[1].width} px wide`
              );
            }
          }
        }
      }
      const plain = await read(
        render('en', { confirm: dialogs[0].confirm, busy: undefined }, true)
      );
      if (plain.status !== null || plain.announced.length !== 0) {
        problems.push('a dialog without a busy line draws one');
      }
      await page.close();
      assert.deepEqual(problems, [], problems.join('\n'));
    });
  }
);
