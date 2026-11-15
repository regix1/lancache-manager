import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree } from './component-tree.mjs';
import { make, translator } from './transpile-module.mjs';

/**
 * While sign-in is on, the Users page sign-in pane shows whether `/metrics` asks for the API key,
 * because a public endpoint answers anyone whatever the sign-in method says. The row's switch
 * follows the Grafana card: it reads the current state, takes each `MetricsSecurityUpdated` event,
 * reads again after the socket comes back (an event sent while it was down never arrives), and shows
 * a failed read with Retry. The shipped row, the feature components it draws and the shipped
 * reconnect hook run on a hook store.
 */

const ROW = 'src/components/features/user/MetricsAccessRow.tsx';
const translate = translator('en');

const named = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};
const UserSettingRow = named('UserSettingRow');
const ErrorBlock = named('ErrorBlock');
const HelpPopover = named('HelpPopover');
const LoadingSpinner = named('LoadingSpinner');
const ToggleSwitch = named('ToggleSwitch');

/** The values the drawn switches show, and the error boxes. */
const read = (node) => {
  const values = [];
  const errors = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      if (value.type === ErrorBlock) errors.push(value);
      if (value.type === ToggleSwitch) values.push(value.props.value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  return { values, errors };
};

test('the sign-in pane draws the metrics row while sign-in is on, and not while it is off', () => {
  const MetricsAccessRow = named('MetricsAccessRow');
  const drawsRow = (authenticationEnabled) => {
    const { hooks, draw } = hookStore();
    const Pane = make('src/components/features/user/SignInPane.tsx', 'SignInPane', {
      React: { ...React, ...hooks },
      ...hooks,
      useTranslation: () => ({ t: (key) => key }),
      useAuth: () => ({
        isMainAdmin: true,
        authenticationEnabled,
        accountMode: authenticationEnabled ? 'password' : 'unauthenticated',
        signInModeFromEnvironment: false,
        loginServices: [],
        refreshAuth: async () => undefined
      }),
      useErrorHandler: () => ({ notifyError: () => undefined }),
      useHeldValue: (value) => value,
      usesOidc: () => false,
      AccessSetup: named('AccessSetup'),
      ActionMenuItem: named('ActionMenuItem'),
      Alert: named('Alert'),
      Button: named('Button'),
      HelpPopover,
      PasswordField: named('PasswordField'),
      RowActionsMenu: named('RowActionsMenu'),
      ConfirmationModal: named('ConfirmationModal'),
      ApiService: {},
      authService: {},
      API_BASE: '/api',
      getErrorMessage: (error) => error.message,
      UserPaneHeader: named('UserPaneHeader'),
      UserPaneSection: ({ children }) => React.createElement(React.Fragment, null, children),
      UserSettingRow,
      MetricsAccessRow
    });
    let found = false;
    const walk = (value) => {
      if (Array.isArray(value)) value.forEach(walk);
      else if (React.isValidElement(value)) {
        if (value.type === MetricsAccessRow) found = true;
        Object.values(value.props).forEach(walk);
      }
    };
    walk(draw(() => Pane({})));
    return found;
  };
  const wrong = [];
  if (!drawsRow(true))
    wrong.push('with sign-in on, the pane does not say whether /metrics asks for the key');
  if (drawsRow(false)) wrong.push('with sign-in off, the pane draws the metrics row');
  assert.deepEqual(wrong, [], wrong.join('; '));
});

test('the sign-in pane shows whether the metrics endpoint asks for the key and keeps it current', async () => {
  const handlers = new Map();
  let serverAnswer = {
    requiresAuthentication: false,
    source: 'default',
    canToggle: true,
    envVarValue: false
  };
  let failNext = false;
  let reads = 0;
  let isConnected = true;

  const { hooks, draw } = hookStore();
  const useReconnectRefetch = make('src/hooks/useReconnectRefetch.ts', 'useReconnectRefetch', {
    ...hooks
  });
  const { Root: Row, draw: drawFeatures } = await featureTree(ROW, 'MetricsAccessRow', {
    React: { ...React, ...hooks },
    ...hooks,
    ToggleSwitch,
    useAuth: () => ({ isAdmin: true }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useTranslation: () => ({ t: translate }),
    useSignalR: () => ({
      on: (name, handler) => handlers.set(name, handler),
      off: (name) => handlers.delete(name),
      isConnected
    }),
    useReconnectRefetch,
    ApiService: {
      getMetricsSecurity: async () => {
        reads += 1;
        if (failNext) {
          failNext = false;
          throw new Error('the server could not be reached');
        }
        return serverAnswer;
      }
    },
    getErrorMessage: (error) => error.message,
    isAbortError: () => false,
    LoadingSpinner,
    ErrorBlock,
    HelpPopover,
    UserSettingRow
  });
  const settle = async () => {
    let drawn;
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      drawn = draw(() => drawFeatures(Row({})));
    }
    return read(drawn);
  };

  const wrong = [];
  const expect = (label, shown, value) => {
    if (shown.errors.length !== 0) wrong.push(`${label}: it shows a failed read`);
    else if (shown.values.length !== 1 || shown.values[0] !== value)
      wrong.push(`${label}: its switch shows ${JSON.stringify(shown.values)}, not ${value}`);
  };

  expect('on first draw', await settle(), 'public');

  serverAnswer = { ...serverAnswer, requiresAuthentication: true, source: 'ui' };
  handlers.get('MetricsSecurityUpdated')?.(serverAnswer);
  expect('after the switch was turned on', await settle(), 'secured');

  // The switch goes back to Public while the socket is down, so no event reaches this page.
  isConnected = false;
  await settle();
  serverAnswer = { ...serverAnswer, requiresAuthentication: false };
  const readsBeforeReconnect = reads;
  isConnected = true;
  expect('after the socket came back', await settle(), 'public');
  if (reads === readsBeforeReconnect)
    wrong.push('the socket came back and the row did not read again');

  // A read that fails shows the failure with Retry, and Retry brings the row back.
  failNext = true;
  isConnected = false;
  await settle();
  isConnected = true;
  const failed = await settle();
  if (failed.errors.length !== 1) wrong.push('a failed read shows no error box');
  else {
    serverAnswer = { ...serverAnswer, requiresAuthentication: true };
    await failed.errors[0].props.onRetry();
    expect('after Retry', await settle(), 'secured');
  }

  assert.deepEqual(wrong, [], `the sign-in pane's metrics row is wrong: ${wrong.join('; ')}`);
});
