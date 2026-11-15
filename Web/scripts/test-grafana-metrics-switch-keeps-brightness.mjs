import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree } from './component-tree.mjs';
import { busyChange, LoadingSpinner } from './markup-shape.mjs';
import { make, translator } from './transpile-module.mjs';

/**
 * The Grafana card's Public/Secured switch saves when it is clicked. While that save runs the switch
 * keeps its box and its brightness: it does not fade, which reads as a control that broke for a
 * moment. The shipped card runs on a hook store, drawn with the feature components it draws, with its reads answered; the admin clicks Secured, the
 * save is held, and the switch the card draws then is compared with the one it draws once the save
 * answers, through the shipped switch.
 */

const CARD = 'src/components/features/management/grafana/GrafanaEndpoints.tsx';
const translate = translator('en');
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);
const ToggleSwitch = make('src/components/ui/ToggleSwitch.tsx', 'ToggleSwitch', {
  React,
  LoadingSpinner,
  Tooltip: passthrough
});

/** The one switch element in a drawn tree. */
const switchIn = (node) => {
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      if (value.type === ToggleSwitch) found.push(value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  assert.equal(found.length, 1, `the card drew ${found.length} switches`);
  return found[0];
};

test('the Grafana metrics switch keeps its box and brightness while its save runs', async () => {
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
  const shippedFetch = globalThis.fetch;
  globalThis.fetch = async (url) =>
    String(url).endsWith('/interval') ? answer({ interval: 15 }) : answer({ gameLimit: 50 });

  try {
    const { hooks, draw } = hookStore();
    const { Root: Card, draw: drawFeatures } = await featureTree(CARD, 'GrafanaEndpoints', {
      React: { ...React, ...hooks },
      ...hooks,
      ToggleSwitch,
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
          new Promise((resolve) => saves.push(() => resolve(security(requiresAuthentication)))),
        getFetchOptions: (options = {}) => options
      },
      assertOk: async (response) => {
        if (!response.ok) throw new Error(`status ${response.status}`);
      },
      isAbortError: () => false,
      getErrorMessage: (error) => error.message,
      copyText: async () => true
    });
    const drawn = () => switchIn(draw(() => drawFeatures(Card({}))));
    const settle = async () => {
      for (let pass = 0; pass < 6; pass += 1) {
        await turn();
        draw(() => drawFeatures(Card({})));
      }
    };

    await settle();
    // The admin clicks Secured; the save is held until the test answers it.
    const clicked = drawn().props.onChange('secured');
    await settle();
    const saving = drawn();
    assert.equal(saves.length, 1, 'the click did not start a save');
    saves[0]();
    await clicked;
    await settle();
    const saved = drawn();

    const { restOutline, busyOutline, addedDimming } = busyChange(
      React.createElement(ToggleSwitch, saved.props),
      React.createElement(ToggleSwitch, saving.props)
    );
    const wrong = [];
    if (busyOutline !== restOutline)
      wrong.push(`its box changes: saved ${restOutline}, saving ${busyOutline}`);
    if (addedDimming.length > 0) wrong.push(`it fades: saving adds ${addedDimming.join(' ')}`);
    assert.deepEqual(
      wrong,
      [],
      `the Grafana metrics switch changes while its save runs: ${wrong.join('; ')}`
    );
  } finally {
    globalThis.fetch = shippedFetch;
  }
});
