import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { translator } from './transpile-module.mjs';

/**
 * The Grafana card has two controls that save the metrics access mode: the Public/Secured switch and
 * Reset to default. Only the control whose save runs shows busy: a Reset spins the Reset button and
 * leaves the switch alone, and a switch save leaves Reset bright and enabled (its handler ignores a
 * click while either save runs). The shipped card runs on a hook store, drawn with the feature components it draws, with its reads answered and
 * every save held until the test answers it.
 */

const CARD = 'src/components/features/management/grafana/GrafanaEndpoints.tsx';
const translate = translator('en');
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);

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

const mountCard = async () => {
  const security = (requiresAuthentication) => ({
    requiresAuthentication,
    source: 'ui',
    canToggle: true,
    envVarValue: false
  });
  const saves = [];
  const answer = (body) =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  globalThis.window ??= { location: { origin: 'http://lancache.test' } };
  globalThis.fetch = async (url) =>
    String(url).endsWith('/interval') ? answer({ interval: 15 }) : answer({ gameLimit: 50 });

  const { hooks, draw } = hookStore();
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const { Root: Card, draw: drawFeatures } = await featureTree(CARD, 'GrafanaEndpoints', {
    React: { ...React, ...hooks },
    ...hooks,
    ToggleSwitch,
    Button,
    AccordionSection: passthrough,
    useTranslation: () => ({ t: translate }),
    useAuth: () => ({ isAdmin: true }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    useAccordionGroupItem: () => undefined,
    useCopyFeedback: () => [null, () => undefined],
    ApiService: {
      getMetricsSecurity: async () => security(false),
      setMetricsSecurity: (requiresAuthentication) =>
        new Promise((resolve) =>
          saves.push(() => resolve(security(requiresAuthentication === true)))
        ),
      getFetchOptions: (options = {}) => options
    },
    assertOk: async (response) => {
      if (!response.ok) throw new Error(`status ${response.status}`);
    },
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    copyText: async () => true
  });
  const tree = () => draw(() => drawFeatures(Card({})));
  const controls = () => {
    const drawn = tree();
    const switches = elementsIn(drawn, (element) => element.type === ToggleSwitch);
    const resets = elementsIn(
      drawn,
      (element) => element.type === Button && element.props.className === 'metrics-reset-button'
    );
    assert.equal(switches.length, 1, `the card drew ${switches.length} switches`);
    assert.equal(resets.length, 1, `the card drew ${resets.length} Reset buttons`);
    return { toggle: switches[0].props, reset: resets[0].props };
  };
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      tree();
    }
  };
  return { controls, settle, saves };
};

test('a Reset to default spins Reset, not the switch', async () => {
  const shippedFetch = globalThis.fetch;
  try {
    const card = await mountCard();
    await card.settle();
    const clicked = card.controls().reset.onClick();
    await card.settle();
    const { toggle, reset } = card.controls();
    assert.equal(card.saves.length, 1, 'the Reset click did not start a save');
    const wrong = [];
    if (reset.loading !== true) wrong.push('Reset shows no busy sign');
    if (toggle.loading === true) wrong.push('the switch spins for a save it did not start');
    card.saves[0]();
    await clicked;
    await card.settle();
    assert.deepEqual(wrong, [], `while Reset to default runs: ${wrong.join('; ')}`);
    assert.notEqual(card.controls().reset.loading, true, 'Reset still spins after its save');
  } finally {
    globalThis.fetch = shippedFetch;
  }
});

test('a switch save leaves Reset bright, and a Reset click then starts nothing', async () => {
  const shippedFetch = globalThis.fetch;
  try {
    const card = await mountCard();
    await card.settle();
    const clicked = card.controls().toggle.onChange('secured');
    await card.settle();
    const { reset } = card.controls();
    assert.equal(card.saves.length, 1, 'the switch click did not start a save');
    const wrong = [];
    if (reset.disabled === true) wrong.push('Reset is disabled, so it fades');
    if (reset.loading === true) wrong.push('Reset spins for a save it did not start');
    await reset.onClick();
    if (card.saves.length !== 1) wrong.push('a Reset click during the switch save started a save');
    card.saves[0]();
    await clicked;
    await card.settle();
    assert.deepEqual(wrong, [], `while the switch saves: ${wrong.join('; ')}`);
  } finally {
    globalThis.fetch = shippedFetch;
  }
});
