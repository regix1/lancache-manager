import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { translator } from './transpile-module.mjs';

/**
 * The Users page sign-in pane's Prometheus Metrics row lets an admin change the `/metrics` access
 * mode in place, with the Public / Secured switch and Reset to default the Grafana card has, saved
 * through the same admin-only call. Anyone who is not an admin sees the state and no control that
 * saves it. The shipped row runs on a hook store, drawn with the feature components it draws.
 */

const ROW = 'src/components/features/user/MetricsAccessRow.tsx';
const translate = translator('en');
const RESET_LABEL = translate('management.grafana.metricsToggle.resetToDefault');

/** The elements in a drawn tree that `matches` accepts. */
const elementsIn = (node, matches) => {
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      if (matches(value)) found.push(value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  return found;
};

const mountRow = async (auth) => {
  const saves = [];
  let server = { requiresAuthentication: true, source: 'ui', canToggle: true, envVarValue: false };
  const { hooks, draw } = hookStore();
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const { Root: Row, draw: drawFeatures } = await featureTree(ROW, 'MetricsAccessRow', {
    React: { ...React, ...hooks },
    ...hooks,
    ToggleSwitch,
    Button,
    useTranslation: () => ({ t: translate }),
    useAuth: () => auth,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    ApiService: {
      getMetricsSecurity: async () => server,
      setMetricsSecurity: async (requiresAuthentication) => {
        saves.push(requiresAuthentication);
        server =
          requiresAuthentication === null
            ? { ...server, requiresAuthentication: false, source: 'default' }
            : { ...server, requiresAuthentication, source: 'ui' };
        return server;
      },
      getFetchOptions: (options = {}) => options
    },
    getErrorMessage: (error) => error.message,
    isAbortError: () => false
  });
  const tree = () => draw(() => drawFeatures(Row({})));
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      tree();
    }
  };
  const controls = () => {
    const drawn = tree();
    return {
      switches: elementsIn(drawn, (element) => element.type === ToggleSwitch).map(
        (element) => element.props
      ),
      resets: elementsIn(
        drawn,
        (element) =>
          element.type === Button &&
          (element.props.className === 'metrics-reset-button' ||
            element.props.children === RESET_LABEL)
      ).map((element) => element.props)
    };
  };
  return { settle, controls, saves };
};

const ADMIN = {
  isAdmin: true,
  isMainAdmin: true,
  authenticationEnabled: true,
  sessionType: 'admin',
  authMode: 'authenticated'
};
const MEMBER = {
  isAdmin: true,
  isMainAdmin: false,
  authenticationEnabled: true,
  sessionType: 'user',
  authMode: 'authenticated'
};

test('an admin changes the metrics access mode from the sign-in pane', async () => {
  const row = await mountRow(ADMIN);
  await row.settle();
  const wrong = [];
  const first = row.controls();
  if (first.switches.length !== 1)
    wrong.push(`the row draws ${first.switches.length} Public / Secured switches, not one`);
  else {
    const toggle = first.switches[0];
    if (toggle.disabled === true) wrong.push('the switch is disabled for an admin');
    if (toggle.value !== 'secured') wrong.push(`the switch shows ${toggle.value}, not secured`);
    await toggle.onChange('public');
    await row.settle();
    if (row.saves[0] !== false) wrong.push(`turning it Public saved ${JSON.stringify(row.saves)}`);
    const after = row.controls().switches[0];
    if (after?.value !== 'public') wrong.push(`after the save the switch shows ${after?.value}`);
  }
  const resets = row.controls().resets;
  if (resets.length !== 1)
    wrong.push(`the row draws ${resets.length} Reset to default buttons, not one`);
  else {
    const savesBefore = row.saves.length;
    await resets[0].onClick();
    await row.settle();
    if (row.saves.length !== savesBefore + 1 || row.saves.at(-1) !== null)
      wrong.push(`Reset to default saved ${JSON.stringify(row.saves.slice(savesBefore))}`);
  }
  assert.deepEqual(wrong, [], `the sign-in pane's metrics row: ${wrong.join('; ')}`);
});

test('anyone who is not an admin sees no control that saves the metrics access mode', async () => {
  const row = await mountRow(MEMBER);
  await row.settle();
  const { switches, resets } = row.controls();
  const wrong = [];
  for (const toggle of switches.filter((props) => props.disabled !== true)) {
    wrong.push('the row draws a switch a member can change');
    await toggle.onChange('public');
  }
  for (const reset of resets.filter((props) => props.disabled !== true)) {
    wrong.push('the row draws a Reset to default a member can press');
    await reset.onClick();
  }
  await row.settle();
  if (row.saves.length !== 0) wrong.push(`a member's click saved ${JSON.stringify(row.saves)}`);
  assert.deepEqual(wrong, [], `the sign-in pane's metrics row for a member: ${wrong.join('; ')}`);
});
