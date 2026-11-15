import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { translator } from './transpile-module.mjs';

/**
 * The Users page sign-in pane changes `/metrics` access with the Grafana card's own control: the
 * Public / Secured switch, the line naming where the rule comes from, and Reset to default. The
 * switch saves through the same `setMetricsSecurity` call, shows what the server answered, follows a
 * change the server pushes from the Grafana card, and only the control whose save runs shows busy.
 * The shipped row runs on a hook store with the feature components it draws; saves are held until
 * the test answers them.
 */

const ROW = 'src/components/features/user/MetricsAccessRow.tsx';
const translate = translator('en');

/** The elements in a drawn tree that `matches` accepts, and every string it draws. */
const read = (node, matches) => {
  const found = [];
  const texts = [];
  const walk = (value) => {
    if (typeof value === 'string') texts.push(value);
    else if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      if (matches(value)) found.push(value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  return { found, texts };
};

/** A server answer for the access state. */
const access = (requiresAuthentication, source) => ({
  requiresAuthentication,
  source,
  canToggle: true,
  envVarValue: false
});

const mountRow = async () => {
  const server = access(false, 'default');
  const saves = [];
  const handlers = new Map();
  const { hooks, draw } = hookStore();
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const { Root: Row, draw: drawFeatures } = await featureTree(ROW, 'MetricsAccessRow', {
    React: { ...React, ...hooks },
    ...hooks,
    ToggleSwitch,
    Button,
    UserSettingRow: ({ children }) => React.createElement(React.Fragment, null, children),
    useTranslation: () => ({ t: translate }),
    useAuth: () => ({ isAdmin: true }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSignalR: () => ({
      on: (name, handler) => handlers.set(name, handler),
      off: (name) => handlers.delete(name),
      isConnected: true
    }),
    useReconnectRefetch: () => undefined,
    ApiService: {
      getMetricsSecurity: async () => ({ ...server }),
      setMetricsSecurity: (enabled) =>
        new Promise((resolve) => saves.push({ enabled, answer: resolve }))
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
  const shown = () => {
    const drawn = tree();
    const switches = read(drawn, (element) => element.type === ToggleSwitch).found;
    const resets = read(
      drawn,
      (element) => element.type === Button && element.props.className === 'metrics-reset-button'
    ).found;
    return {
      toggle: switches.length === 1 ? switches[0].props : null,
      reset: resets.length === 1 ? resets[0].props : null,
      texts: read(drawn, () => false).texts
    };
  };
  const push = (pushed) => handlers.get('MetricsSecurityUpdated')(pushed);
  return { saves, settle, shown, push };
};

test('the sign-in pane switches /metrics between Public and Secured and resets it, as the Grafana card does', async () => {
  const row = await mountRow();
  await row.settle();
  const first = row.shown();
  assert.ok(first.toggle, 'the sign-in pane draws no Public / Secured switch');
  assert.ok(first.reset, 'the sign-in pane draws no Reset to default');

  const wrong = [];
  if (first.toggle.value !== 'public') wrong.push(`it starts at ${first.toggle.value}, not public`);
  if (!first.texts.includes(translate('management.grafana.metricsToggle.source.default')))
    wrong.push('it does not say the rule is the default');
  if (first.toggle.disabled === true) wrong.push('the switch is disabled for the admin');
  if (first.reset.disabled !== true) wrong.push('Reset is enabled with no override to reset');

  // The admin turns the key on: the save goes out, only the switch shows busy, and the row ends on
  // what the server answered.
  const clicked = first.toggle.onChange('secured');
  await row.settle();
  const saving = row.shown();
  const sent = JSON.stringify(row.saves.map((save) => save.enabled));
  if (row.saves.length !== 1 || row.saves[0].enabled !== true)
    wrong.push(`the switch sent ${sent}, not [true]`);
  if (saving.toggle.loading !== true) wrong.push('the switch shows no busy sign while it saves');
  if (saving.reset.loading === true) wrong.push('Reset spins for the switch save');
  row.saves[0]?.answer(access(true, 'ui'));
  await clicked;
  await row.settle();
  const saved = row.shown();
  if (saved.toggle.value !== 'secured') wrong.push(`after the save it shows ${saved.toggle.value}`);
  if (!saved.texts.includes(translate('management.grafana.metricsToggle.source.ui')))
    wrong.push('after the save it does not say the rule is set in the app');
  if (saved.reset.disabled === true) wrong.push('Reset stays disabled with an override set');

  // Reset to default goes through the same call with no value.
  const reset = saved.reset.onClick();
  await row.settle();
  if (row.saves.length !== 2 || row.saves[1].enabled !== null)
    wrong.push('Reset to default did not send the cleared value');
  row.saves[1]?.answer(access(false, 'default'));
  await reset;
  await row.settle();
  if (row.shown().toggle.value !== 'public') wrong.push('after Reset it does not show public');

  // A change saved on the Grafana card reaches this row through the server's push.
  row.push(access(true, 'ui'));
  await row.settle();
  if (row.shown().toggle.value !== 'secured')
    wrong.push('a change pushed from the Grafana card does not show');

  assert.deepEqual(wrong, [], `the sign-in pane's metrics control is wrong: ${wrong.join('; ')}`);
});
