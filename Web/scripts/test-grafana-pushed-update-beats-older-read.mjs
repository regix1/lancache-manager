import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { translator } from './transpile-module.mjs';

/**
 * The Grafana card reads the metrics access state, the refresh rate and the exported game count
 * together. When another window changes the access state while such a read is out, the server pushes
 * `MetricsSecurityUpdated`, and the read that left before the change must not put the old access
 * state back when it lands. Its refresh rate and game count still land: they did not change, and a
 * card that dropped them would show the defaults as if they were saved. The shipped card runs on a
 * hook store with the feature components it draws; the read's answers are held until the test sends
 * them, each with the state the server held when the read left.
 */

const CARD = 'src/components/features/management/grafana/GrafanaEndpoints.tsx';
const translate = translator('en');
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);

const security = (requiresAuthentication) => ({
  requiresAuthentication,
  source: 'ui',
  canToggle: true,
  envVarValue: false
});

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
  const server = { security: security(true), interval: 15, gameLimit: 50 };
  const held = [];
  let holding = false;
  let reconnect = null;
  const handlers = new Map();
  const answer = (body) =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  /** Answers now, or with what the server holds now once the test releases it. */
  const reply = (value) => {
    if (!holding) return Promise.resolve(value);
    return new Promise((resolve) => held.push(() => resolve(value)));
  };
  globalThis.window ??= { location: { origin: 'http://lancache.test' } };
  globalThis.fetch = (url) =>
    reply(
      answer(
        String(url).endsWith('/interval')
          ? { interval: server.interval }
          : { gameLimit: server.gameLimit }
      )
    );

  const { hooks, draw } = hookStore();
  const ToggleSwitch = inert('ToggleSwitch');
  const EnhancedDropdown = inert('EnhancedDropdown');
  const { Root: Card, draw: drawFeatures } = await featureTree(CARD, 'GrafanaEndpoints', {
    React: { ...React, ...hooks },
    ...hooks,
    ToggleSwitch,
    EnhancedDropdown,
    AccordionSection: passthrough,
    useTranslation: () => ({ t: translate }),
    useAuth: () => ({ isAdmin: true }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSignalR: () => ({
      on: (name, handler) => handlers.set(name, handler),
      off: (name) => handlers.delete(name),
      isConnected: true
    }),
    useReconnectRefetch: (_isConnected, refetch) => {
      reconnect = refetch;
    },
    useAccordionGroupItem: () => undefined,
    useCopyFeedback: () => [null, () => undefined],
    ApiService: {
      getMetricsSecurity: () => reply(server.security),
      setMetricsSecurity: async () => server.security,
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
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      tree();
    }
  };
  const shown = () => {
    const drawn = tree();
    const switches = elementsIn(drawn, (element) => element.type === ToggleSwitch);
    assert.equal(switches.length, 1, `the card drew ${switches.length} switches`);
    const dropdownValues = elementsIn(drawn, (element) => element.type === EnhancedDropdown).map(
      (element) => element.props.value
    );
    return { access: switches[0].props.value, dropdownValues };
  };
  return {
    server,
    settle,
    shown,
    hold: () => {
      holding = true;
    },
    release: () => held.splice(0).forEach((open) => open()),
    reconnect: () => reconnect(),
    push: (pushed) => handlers.get('MetricsSecurityUpdated')(pushed)
  };
};

test('a pushed access change beats an older read, and that read still fills the other settings', async () => {
  const shippedFetch = globalThis.fetch;
  try {
    const card = await mountCard();
    await card.settle();
    assert.equal(card.shown().access, 'secured', 'the card did not load the secured state');

    // The connection comes back and the card reads again; the read leaves while the server holds
    // Secured, a refresh rate of 30 seconds and a cap of 100 games.
    card.server.interval = 30;
    card.server.gameLimit = 100;
    card.hold();
    const reading = card.reconnect();
    await card.settle();

    // Another window switches /metrics to Public while the read is out.
    card.server.security = security(false);
    card.push(security(false));
    await card.settle();
    card.release();
    await reading;
    await card.settle();

    const { access, dropdownValues } = card.shown();
    const wrong = [];
    if (access !== 'public')
      wrong.push(
        `the card shows ${access} after the server pushed public and the older read landed`
      );
    for (const expected of ['30', '100'])
      if (!dropdownValues.includes(expected))
        wrong.push(
          `the read's ${expected} did not land (dropdowns show ${dropdownValues.join(', ')})`
        );
    assert.deepEqual(wrong, [], wrong.join('; '));
  } finally {
    globalThis.fetch = shippedFetch;
  }
});
