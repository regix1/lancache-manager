import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * The unmapped removal dialog shares its count figures with the game and service removal dialogs,
 * and the parent resets them the moment either of those opens. Opening one inside the 250 ms fade
 * of the unmapped dialog used to swap the closing dialog's figures for the spinner. The dialog now
 * keeps the figures, the refusal and the Confirm spinner it closed with until the fade ends. The
 * game and service dialog holds its counted figures the same way.
 *
 * The component is compiled as shipped and called as a function once per render. `useState` and
 * `useMemo` are stubbed with call-order slots that persist between renders, because the server
 * renderer cannot carry state from one render to the next.
 */

let slots = [];
globalThis.__heldFiguresReact = React;

const reactStubUrl = moduleUrl(`
  const React = globalThis.__heldFiguresReact;
  export const useState = (initial) => {
    const slots = globalThis.__heldFiguresSlots;
    const index = globalThis.__heldFiguresIndex++;
    if (!(index in slots)) slots[index] = { value: initial };
    return [slots[index].value, (next) => { slots[index].value = next; }];
  };
  export const useMemo = (factory, deps) => {
    const slots = globalThis.__heldFiguresSlots;
    const index = globalThis.__heldFiguresIndex++;
    const slot = slots[index];
    if (slot && slot.deps.every((dep, i) => Object.is(dep, deps[i]))) return slot.value;
    slots[index] = { deps, value: factory() };
    return slots[index].value;
  };
  export default React;
`);

const passThroughUrl = moduleUrl(`
  const React = globalThis.__heldFiguresReact;
  const Box = ({ children }) => React.createElement('div', null, children);
  export const ConfirmationModal = ({ loading, confirmBusy, children }) =>
    React.createElement('div', null, loading || confirmBusy ? 'CONFIRM-BUSY' : 'CONFIRM-IDLE', children);
  export const Alert = Box;
  export const Button = Box;
  export const CustomScrollbar = ({ className, children }) =>
    React.createElement('div', { className }, children);
  export default Box;
`);

const spinnerUrl = moduleUrl(`
  const React = globalThis.__heldFiguresReact;
  export default () => React.createElement('span', null, 'SPINNER');
`);

const i18nUrl = moduleUrl(`
  export const useTranslation = () => ({
    t: (key, options) => (options ? key + ' ' + options.count + ' ' + options.size : key)
  });
`);

const formattersUrl = moduleUrl(`
  export const formatBytes = (bytes) => bytes + ' bytes';
  export const formatCount = (count) => String(count);
`);

const displayNameUrl = moduleUrl(`export const getServiceDisplayName = (service) => service;`);

const compileSource = (relativePath, aliasUrls) =>
  compileToUrl(relativePath, aliasUrls, { jsx: typescript.JsxEmit.React });

const heldValueUrl = await compileSource('../src/hooks/useHeldValue.ts', { react: reactStubUrl });
const { default: UnmappedRemovalModal } = await import(
  await compileSource('../src/components/modals/cache/UnmappedRemovalModal.tsx', {
    react: reactStubUrl,
    'react-i18next': i18nUrl,
    '@components/common/ConfirmationModal': passThroughUrl,
    '@components/common/LoadingSpinner': spinnerUrl,
    '@components/ui/Alert': passThroughUrl,
    '@components/ui/Badge': passThroughUrl,
    '@components/ui/Button': passThroughUrl,
    '@components/ui/CustomScrollbar': passThroughUrl,
    '@hooks/useHeldValue': heldValueUrl,
    '@utils/formatters': formattersUrl,
    '@utils/serviceDisplayName': displayNameUrl
  })
);

const { default: CacheRemovalModal } = await import(
  await compileSource('../src/components/modals/cache/CacheRemovalModal.tsx', {
    react: reactStubUrl,
    'react-i18next': i18nUrl,
    '@components/common/ConfirmationModal': passThroughUrl,
    '@components/ui/Alert': passThroughUrl,
    '@components/ui/Button': passThroughUrl,
    '@hooks/useHeldValue': heldValueUrl,
    '@utils/formatters': formattersUrl
  })
);

const noop = () => undefined;

const render = (props) => {
  globalThis.__heldFiguresIndex = 0;
  globalThis.__heldFiguresSlots = slots;
  return renderToStaticMarkup(
    UnmappedRemovalModal({
      onClose: noop,
      onConfirm: noop,
      onRetry: noop,
      confirming: false,
      error: null,
      failureReason: null,
      statusMessage: 'Counting',
      ...props
    })
  );
};

const countedServices = [{ service: 'steam', file_count: 5, total_bytes: 100 }];
const counted = { files: 5, bytes: 100, services: countedServices, failed: false };
const reset = { files: null, bytes: null, services: [], failed: false };

test('a closing dialog keeps its figures when another dialog resets the shared count', () => {
  slots = [];

  const open = render({ opened: true, ...counted });
  assert.match(open, /modals\.unmappedRemoval\.summary 5 100 bytes/);

  const closing = render({ opened: false, ...counted });
  assert.match(closing, /modals\.unmappedRemoval\.summary 5 100 bytes/);

  const otherDialogOpened = render({ opened: false, ...reset });
  assert.match(otherDialogOpened, /modals\.unmappedRemoval\.summary 5 100 bytes/);
  assert.doesNotMatch(otherDialogOpened, /SPINNER/);
});

test('a closing dialog keeps its refusal when another dialog clears the shared error', () => {
  slots = [];

  render({ opened: true, ...counted, error: 'Log ingestion is behind' });
  render({ opened: false, ...counted, error: 'Log ingestion is behind' });

  const otherDialogOpened = render({ opened: false, ...reset, error: null });
  assert.match(otherDialogOpened, /Log ingestion is behind/);
});

test('Confirm keeps its spinner while the dialog fades after the server accepted', () => {
  slots = [];

  render({ opened: true, ...counted });
  const confirming = render({ opened: true, ...counted, confirming: true });
  assert.match(confirming, /CONFIRM-BUSY/);

  const closing = render({ opened: false, ...counted, confirming: false });
  assert.match(closing, /CONFIRM-BUSY/);
});

test('a closing game dialog keeps its counted figures when another dialog resets the count', () => {
  slots = [];
  const game = {
    game_name: 'Example',
    cache_files_found: 9,
    total_size_bytes: 900,
    depot_ids: []
  };
  const renderGame = (props) => {
    globalThis.__heldFiguresIndex = 0;
    globalThis.__heldFiguresSlots = slots;
    return renderToStaticMarkup(
      CacheRemovalModal({
        onClose: noop,
        onConfirm: noop,
        confirming: false,
        error: null,
        ...props
      })
    );
  };
  const countedGame = {
    fileCount: 5,
    fileBytes: 500,
    statusMessage: 'Counting',
    confirmDisabled: false,
    confirmBusy: false
  };
  const resetGame = {
    fileCount: undefined,
    fileBytes: null,
    statusMessage: 'Counting',
    confirmDisabled: true,
    confirmBusy: true
  };
  const target = { type: 'game', data: game };

  const open = renderGame({ target, ...countedGame });
  assert.match(open, /modals\.cacheRemoval\.summaryGameCounted/);

  const fadingAfterReset = renderGame({ target: null, ...resetGame });
  assert.match(fadingAfterReset, /modals\.cacheRemoval\.summaryGameCounted/);
  assert.match(fadingAfterReset, /CONFIRM-IDLE/);

  // A refused removal keeps the dialog open with the reason, and Confirm busy while it runs.
  slots = [];
  assert.match(renderGame({ target, ...countedGame, confirming: true }), /CONFIRM-BUSY/);
  const refused = renderGame({ target, ...countedGame, error: 'A download is in progress' });
  assert.match(refused, /A download is in progress/);
  assert.match(refused, /CONFIRM-IDLE/);
});

test('a failed count shows the reason the server gave under the failure line', () => {
  slots = [];
  const withReason = render({
    opened: true,
    ...reset,
    failed: true,
    failureReason: 'No cache files'
  });
  assert.match(withReason, /modals\.unmappedRemoval\.countFailed/);
  assert.match(withReason, /No cache files/);

  slots = [];
  const withoutReason = render({ opened: true, ...reset, failed: true });
  assert.match(withoutReason, /modals\.unmappedRemoval\.countFailed<\/p><\/div>/);
});

test('reopening the dialog shows the new count state, not the held figures', () => {
  slots = [];

  render({ opened: true, ...counted });
  render({ opened: false, ...reset });

  const reopened = render({ opened: true, ...reset });
  assert.match(reopened, /SPINNER/);
  assert.match(reopened, /CONFIRM-IDLE/);
  assert.doesNotMatch(reopened, /modals\.unmappedRemoval\.summary/);
});

test('every count state sits in the box that reserves the counted figures room, so the dialog never grows', async () => {
  slots = [];
  const states = {
    counting: reset,
    counted,
    nothingToRemove: { files: 0, bytes: 0, services: [], failed: false },
    failed: { ...reset, failed: true }
  };
  for (const [name, figures] of Object.entries(states)) {
    slots = [];
    assert.match(
      render({ opened: true, ...figures }),
      /<div class="unmapped-removal-count">/,
      `${name} state is inside the count box`
    );
  }

  const css = await readFile(
    new URL('../src/styles/components/modals.css', import.meta.url),
    'utf8'
  );
  // A fixed height, not a floor: a floor holds 2 service rows and grows by a row from the third.
  const rule = css.match(/\.unmapped-removal-count\s*\{([^}]*)\}/);
  assert.ok(rule, 'modals.css defines .unmapped-removal-count');
  assert.match(rule[1], /(^|[\s;])height:\s*8rem/);
  assert.doesNotMatch(rule[1], /(min|max)-height/);
  // The box lays out its own column rather than borrowing the dialog's content class.
  assert.match(rule[1], /display:\s*flex/);
  assert.match(rule[1], /flex-direction:\s*column/);
  assert.match(rule[1], /gap:\s*1rem/);

  // Any number of services scrolls inside that height, in the app's own scroll area, which fills
  // what the summary leaves and scrolls its viewport.
  const list = css.match(/\.unmapped-removal-count__list\s*\{([^}]*)\}/);
  assert.ok(list, 'modals.css defines .unmapped-removal-count__list');
  assert.match(list[1], /flex:\s*1 1 auto/);
  assert.match(list[1], /min-height:\s*0/);
  assert.doesNotMatch(list[1], /overflow/);
  const viewport = css.match(/\.unmapped-removal-count__list > \.csb-viewport\s*\{([^}]*)\}/);
  assert.ok(viewport, 'modals.css sizes the scroll viewport');
  assert.match(viewport[1], /flex:\s*1 1 auto/);
  assert.match(viewport[1], /min-height:\s*0/);

  slots = [];
  const six = Array.from({ length: 6 }, (_, index) => ({
    service: `service${index}`,
    file_count: 1,
    total_bytes: 1
  }));
  assert.match(
    render({ opened: true, files: 6, bytes: 6, services: six, failed: false }),
    /<div class="unmapped-removal-count"><p[^>]*>[^<]*<\/p><div class="unmapped-removal-count__list"><ul class="divided-list">/
  );
  const modal = await readFile(
    new URL('../src/components/modals/cache/UnmappedRemovalModal.tsx', import.meta.url),
    'utf8'
  );
  assert.match(
    modal,
    /<CustomScrollbar\s+maxHeight="none"\s+paddingMode="none"\s+radius="none"\s+className="unmapped-removal-count__list"\s*>/
  );
});
