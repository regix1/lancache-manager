import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import postcss from 'postcss';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import tailwindcss from 'tailwindcss';
import typescript from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * While the live connection to the server is down every request fails for the same reason, so one
 * banner under the top bar speaks for the whole app and each section's own failure box and closed
 * section chip stay quiet. This file runs the shipped hook that decides "the connection is lost",
 * the two shared components that go quiet on it, and the popup sink that carries a failure's
 * reason as its second line.
 */

const state = {
  connectionState: 'connected',
  mockMode: false,
  hasSession: true,
  added: []
};
globalThis.connectionLostTestState = state;

const i18nStub = moduleUrl('export default { t: (key) => key };');

const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});
const { buildApiError } = await import(apiErrorUrl);

const errorUrl = await compileToUrl('../src/utils/error.ts', {
  '@/i18n': i18nStub,
  '../services/apiError': apiErrorUrl
});
const { getErrorMessage } = await import(errorUrl);

/** The hook compiled against context stubs that read the test state, loaded on first use. */
let hook = null;
const loadConnectionLost = async () => {
  if (!hook) {
    hook = await import(
      await compileToUrl('../src/hooks/useConnectionLost.ts', {
        '@contexts/SignalRContext/useSignalR': moduleUrl(
          'export const useSignalR = () => ({ connectionState: globalThis.connectionLostTestState.connectionState });'
        ),
        '@contexts/useMockMode': moduleUrl(
          'export const useMockMode = () => ({ mockMode: globalThis.connectionLostTestState.mockMode, setMockMode: () => undefined });'
        ),
        '@contexts/useAuth': moduleUrl(
          'export const useAuth = () => ({ hasSession: globalThis.connectionLostTestState.hasSession });'
        )
      })
    );
  }
  return hook.useConnectionLost;
};

const h = (type, props, ...children) => ({
  type,
  props: {
    ...props,
    children: children.length <= 1 ? children[0] : children
  }
});

const jsx = {
  jsx: typescript.JsxEmit.React,
  jsxFactory: 'h',
  jsxFragmentFactory: 'Fragment'
};

/** Source text of the arrow a `.tsx` file assigns to the component named `name`. */
const componentArrow = (relativePath, name) => {
  const source = parseSource(relativePath, typescript.ScriptKind.TSX);
  return findSoleNode(
    source,
    `${name} declaration`,
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(source) === name
  ).initializer.getText(source);
};

const Alert = Symbol('Alert');
const Button = Symbol('Button');
const SectionHeaderChip = Symbol('SectionHeaderChip');

const lostCases = [
  { hasSession: false, mockMode: false, connectionState: 'disconnected', lost: false },
  { hasSession: true, mockMode: true, connectionState: 'disconnected', lost: false },
  { hasSession: true, mockMode: false, connectionState: 'connecting', lost: false },
  { hasSession: true, mockMode: false, connectionState: 'connected', lost: false },
  { hasSession: true, mockMode: false, connectionState: 'disconnected', lost: true },
  { hasSession: true, mockMode: false, connectionState: 'reconnecting', lost: true }
];

const setConnection = ({ hasSession, mockMode, connectionState }) => {
  state.hasSession = hasSession;
  state.mockMode = mockMode;
  state.connectionState = connectionState;
};

test('the connection counts as lost only for a session whose live connection is down', async () => {
  const useConnectionLost = await loadConnectionLost();
  // A guest session has a session too, so it gets the banner the same as a signed-in admin. With
  // no session no socket is ever opened, and mock mode opens none, so neither counts as lost.
  for (const row of lostCases) {
    setConnection(row);
    assert.equal(useConnectionLost(), row.lost, JSON.stringify(row));
  }
});

test('a section failure box stays quiet while the connection is lost', async () => {
  const useConnectionLost = await loadConnectionLost();
  const ErrorBlock = bindLifted(
    componentArrow('src/components/ui/ErrorBlock.tsx', 'ErrorBlock'),
    { useConnectionLost, Alert, Button, h },
    jsx
  );

  const onRetry = () => undefined;
  const props = {
    title: 'Failed to load clients',
    message: 'The server could not complete the request.',
    retryLabel: 'Retry',
    onRetry,
    className: 'section-spacing'
  };

  for (const row of lostCases) {
    setConnection(row);
    const tree = ErrorBlock(props);
    if (row.lost) {
      assert.equal(tree, null, JSON.stringify(row));
      continue;
    }
    // The box is the alert with the reason, and Retry is the alert's own action.
    assert.equal(tree.type, Alert, `no alert for ${JSON.stringify(row)}`);
    // A caller's spacing class rides on the box, so it goes away when the box does.
    assert.equal(tree.props.className, 'w-full section-spacing');
    assert.equal(tree.props.color, 'error');
    assert.equal(tree.props.title, props.title);
    assert.equal(tree.props.children.props.children, props.message);
    const retry = tree.props.action;
    assert.equal(retry.type, Button);
    assert.equal(retry.props.onClick, onRetry);
    assert.equal(retry.props.children, props.retryLabel);
  }
});

test('Retry renders inside the red box, not below it', async () => {
  const useConnectionLost = await loadConnectionLost();
  setConnection({ hasSession: true, mockMode: false, connectionState: 'connected' });
  const onRetry = () => undefined;

  const ErrorBlock = bindLifted(
    componentArrow('src/components/ui/ErrorBlock.tsx', 'ErrorBlock'),
    { useConnectionLost, Alert, Button, h },
    jsx
  );
  const box = ErrorBlock({ title: 'Failed', message: 'Reason', retryLabel: 'Retry', onRetry });
  assert.equal(box.type, Alert, 'the block is the alert itself, with nothing below it');
  assert.equal(box.props.className, 'w-full', 'no layout class from the caller adds none');

  // The shipped Alert draws its action inside its own root, after the text.
  const ShippedAlert = bindLifted(
    componentArrow('src/components/ui/Alert.tsx', 'Alert'),
    {
      COLOR_TO_CLASS: { error: 'alert-error' },
      DEFAULT_ICONS: { error: 'icon' },
      X: Symbol('X'),
      h
    },
    jsx
  );
  const root = ShippedAlert(box.props);
  assert.match(root.props.className, /^alert alert-error /);
  const slot = root.props.children.find((child) => child?.props?.className === 'alert-action');
  assert.ok(slot, 'the alert has no action slot inside its box');
  assert.equal(slot.props.children.type, Button);
  assert.equal(slot.props.children.props.onClick, onRetry);

  // The startup card puts its Retry in the same place and keeps its extra form below the box.
  const StartupErrorCard = bindLifted(
    componentArrow('src/components/common/StartupErrorCard.tsx', 'StartupErrorCard'),
    { useTranslation: () => ({ t: (key) => key }), Alert, Button, h },
    jsx
  );
  const form = { type: 'form' };
  const screen = StartupErrorCard({ title: 'Failed', message: 'Reason', onRetry, children: form });
  const [cardAlert, below] = screen.props.children.props.children;
  assert.equal(cardAlert.type, Alert);
  assert.equal(cardAlert.props.action.type, Button);
  assert.equal(cardAlert.props.action.props.onClick, onRetry);
  assert.equal(cardAlert.props.action.props.children, 'common.retry');
  assert.equal(below, form);
});

test('the box action sits at the right edge, centered, and wraps before the text gets unreadable', () => {
  const sheet = postcss.parse(
    readFileSync(new URL('../src/styles/components/alerts.css', import.meta.url), 'utf8')
  );
  const declared = {};
  sheet.walkRules((rule) => {
    if (!rule.selectors.some((selector) => selector.includes('alert-action'))) return;
    // No width-specific rule: the row wraps only when the box itself is too narrow.
    assert.notEqual(rule.parent.type === 'atrule' && rule.parent.name, 'media', rule.selector);
    declared[rule.selector] = Object.fromEntries(rule.nodes.map((node) => [node.prop, node.value]));
  });

  const action = declared['.alert-action'];
  assert.equal(action['align-self'], 'center');
  assert.equal(action['flex-shrink'], '0');
  assert.equal(action['margin-left'], 'auto', 'on its own row it keeps to the right edge');
  assert.equal(action['margin-top'], undefined, 'no offset pulls it toward the title line');
  assert.equal(action.gap, '0.5rem', 'two controls in the slot need space between them');

  // The text keeps a readable width beside the control; below it, the control takes its own row.
  assert.equal(declared['.alert:has(> .alert-action)']['flex-wrap'], 'wrap');
  const basis = declared['.alert:has(> .alert-action) > .alert-content']['flex-basis'];
  assert.match(basis, /^\d+(\.\d+)?rem$/);
  assert.ok(parseFloat(basis) >= 8, `the text column minimum ${basis} splits words`);
});

test('the crash card title stays a level-2 heading inside the red box', () => {
  const reactJsx = { jsx: typescript.JsxEmit.React };
  const boundary = parseSource(
    'src/components/common/ErrorBoundary.tsx',
    typescript.ScriptKind.TSX
  );
  const render = findSoleNode(
    boundary,
    'ErrorBoundary render method',
    (node) => typescript.isMethodDeclaration(node) && node.name.getText(boundary) === 'render'
  ).getText(boundary);
  const ShippedAlert = bindLifted(
    componentArrow('src/components/ui/Alert.tsx', 'Alert'),
    { React, COLOR_TO_CLASS: { error: 'alert-error' }, DEFAULT_ICONS: { error: null }, X: null },
    reactJsx
  );
  const renderCrash = bindLifted(
    `function ${render}`,
    {
      React,
      i18n: { t: (key) => key },
      Alert: ShippedAlert,
      Button: ({ children }) => React.createElement('button', null, children)
    },
    reactJsx
  );

  const markup = renderToStaticMarkup(renderCrash.call({ state: { hasError: true }, props: {} }));
  assert.match(
    markup,
    /<div class="alert-content"><div class="font-medium mb-1"><h2>common\.errorBoundary\.title<\/h2><\/div>/
  );
});

test('an open section whose only content is a hidden failure box draws no empty body', async () => {
  const useConnectionLost = await loadConnectionLost();
  const reactJsx = { jsx: typescript.JsxEmit.React };
  const plain =
    (tag) =>
    ({ children }) =>
      React.createElement(tag, null, children);

  const ErrorBlock = bindLifted(
    componentArrow('src/components/ui/ErrorBlock.tsx', 'ErrorBlock'),
    { React, useConnectionLost, Alert: plain('section'), Button: plain('button') },
    reactJsx
  );
  const CollapsibleRegion = bindLifted(
    componentArrow('src/components/ui/CollapsibleRegion.tsx', 'CollapsibleRegion'),
    { React, ...React, UNMOUNT_FALLBACK_MS: 430 },
    reactJsx
  );
  const AccordionSection = bindLifted(
    componentArrow('src/components/ui/AccordionSection.tsx', 'AccordionSection'),
    {
      React,
      useTranslation: () => ({ t: (key) => key }),
      ChevronDown: () => null,
      formatCount: String,
      themeColorVar: () => 'var(--theme-accent)',
      Button: plain('button'),
      CollapsibleRegion
    },
    reactJsx
  );
  const openSection = () =>
    renderToStaticMarkup(
      React.createElement(
        AccordionSection,
        { title: 'Accounts', isExpanded: true, onToggle: () => undefined },
        React.createElement(ErrorBlock, {
          title: 'Failed',
          message: 'Reason',
          retryLabel: 'Retry',
          onRetry: () => undefined
        })
      )
    );
  const emptyBody = /<div class="[^"]*\bempty:hidden"><\/div>/;

  // React leaves no whitespace or wrapper node, so the body matches :empty.
  setConnection({ hasSession: true, mockMode: false, connectionState: 'disconnected' });
  assert.match(openSection(), emptyBody);
  setConnection({ hasSession: true, mockMode: false, connectionState: 'connected' });
  assert.doesNotMatch(openSection(), emptyBody);

  // And the class hides an empty element, so no padding or border strip is drawn.
  const { css } = await postcss([
    tailwindcss({
      content: [{ raw: '<div class="empty:hidden"></div>' }],
      corePlugins: { preflight: false }
    })
  ]).process('@tailwind utilities;', { from: undefined });
  assert.match(css, /\.empty\\:hidden:empty\s*\{\s*display:\s*none;?\s*\}/);
});

test('the Prefill page container around the Docker box goes away with the box', () => {
  const app = parseSource('src/App.tsx', typescript.ScriptKind.TSX);
  const box = findSoleNode(
    app,
    'Docker check ErrorBlock',
    (node) =>
      typescript.isJsxSelfClosingElement(node) &&
      node.tagName.getText(app) === 'ErrorBlock' &&
      node.getText(app).includes('app.prefill.dockerCheckFailed')
  );
  // The box is the container's only child, so the container is :empty while the box hides.
  const container = box.parent;
  assert.ok(typescript.isJsxElement(container));
  assert.deepEqual(
    container.children.filter((child) => !typescript.isJsxText(child) || child.getText(app).trim()),
    [box]
  );
  assert.match(container.openingElement.getText(app), /className="[^"]*\bempty:hidden\b/);
});

test('a closed section chip stays quiet while the connection is lost', async () => {
  const useConnectionLost = await loadConnectionLost();
  const source = parseSource(
    'src/components/ui/SectionHeaderActions.tsx',
    typescript.ScriptKind.TSX
  );
  const declaration = findSoleNode(
    source,
    'SectionErrorChip function',
    (node) =>
      typescript.isFunctionDeclaration(node) && node.name?.getText(source) === 'SectionErrorChip'
  )
    .getText(source)
    .replace(/^export\s+/, '');
  const SectionErrorChip = bindLifted(
    declaration,
    {
      useConnectionLost,
      useTranslation: () => ({ t: (key) => key }),
      SectionHeaderChip,
      h
    },
    jsx
  );

  for (const row of lostCases) {
    setConnection(row);
    const chip = SectionErrorChip();
    if (row.lost) {
      assert.equal(chip, null, JSON.stringify(row));
      continue;
    }
    assert.equal(chip.type, SectionHeaderChip);
    assert.equal(chip.props.variant, 'error');
    assert.equal(chip.props.children, 'common.failedToLoad');
  }
});

test('a popup carries the failure reason as its second line, and only when there is one', async () => {
  const { useErrorHandler } = await import(
    await compileToUrl('../src/hooks/useErrorHandler.ts', {
      react: moduleUrl('export const useCallback = (callback) => callback;'),
      '@contexts/notifications': moduleUrl(
        'export const useNotifications = () => ({ addNotification: (notification) => globalThis.connectionLostTestState.added.push(notification) });'
      ),
      '@utils/error': errorUrl
    })
  );
  const { notifyError } = useErrorHandler();
  const failure = await buildApiError(
    new Response(JSON.stringify({ error: 'The cache path is not writable.' }), { status: 500 })
  );

  const quiet = console.error;
  console.error = () => undefined;
  try {
    state.added = [];
    notifyError('Failed to save the schedule', failure);
    assert.equal(state.added.length, 1);
    assert.equal(state.added[0].message, 'Failed to save the schedule');
    assert.equal(state.added[0].status, 'failed');
    assert.equal(state.added[0].error, getErrorMessage(failure));
    assert.equal(state.added[0].error, 'The cache path is not writable.');

    state.added = [];
    notifyError('Failed to save the schedule');
    assert.equal(state.added.length, 1);
    assert.equal(state.added[0].message, 'Failed to save the schedule');
    assert.equal(state.added[0].error, undefined);
  } finally {
    console.error = quiet;
  }
});
