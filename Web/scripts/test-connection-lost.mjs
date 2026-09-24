import assert from 'node:assert/strict';
import test from 'node:test';
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
  const source = parseSource('src/components/ui/ErrorBlock.tsx', typescript.ScriptKind.TSX);
  const arrow = findSoleNode(
    source,
    'ErrorBlock declaration',
    (node) => typescript.isVariableDeclaration(node) && node.name.getText(source) === 'ErrorBlock'
  ).initializer.getText(source);
  const ErrorBlock = bindLifted(arrow, { useConnectionLost, Alert, Button, h }, jsx);

  const onRetry = () => undefined;
  const props = {
    title: 'Failed to load clients',
    message: 'The server could not complete the request.',
    retryLabel: 'Retry',
    onRetry
  };

  for (const row of lostCases) {
    setConnection(row);
    const tree = ErrorBlock(props);
    if (row.lost) {
      assert.equal(tree, null, JSON.stringify(row));
      continue;
    }
    // The box is the alert with the reason, then the row holding Retry.
    const [alert, retryRow] = tree.props.children;
    assert.equal(alert.type, Alert, `no alert for ${JSON.stringify(row)}`);
    assert.equal(alert.props.color, 'error');
    assert.equal(alert.props.title, props.title);
    assert.equal(alert.props.children.props.children, props.message);
    const retry = retryRow.props.children;
    assert.equal(retry.type, Button);
    assert.equal(retry.props.onClick, onRetry);
    assert.equal(retry.props.children, props.retryLabel);
  }
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
