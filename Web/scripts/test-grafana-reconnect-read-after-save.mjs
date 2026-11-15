import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { ordersOf, translator } from './transpile-module.mjs';

/**
 * After the live connection comes back, the Grafana card reads the metrics access state again with
 * three GETs. When the admin clicks Reset to default or the Public/Secured switch while that read is
 * still out, the card must end on what the click saved, whatever order the server answers the read's
 * GETs and the save in. The shipped card runs on a hook store, drawn with the feature components it
 * draws; the read's GETs and the save are each held until the test answers them, and the security GET
 * answers with the state the server holds at that moment.
 */

const CARD = 'src/components/features/management/grafana/GrafanaEndpoints.tsx';
const translate = translator('en');
const RESET_LABEL = translate('management.grafana.metricsToggle.resetToDefault');
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

const SECURED_IN_APP = {
  requiresAuthentication: true,
  source: 'ui',
  canToggle: true,
  envVarValue: false
};

/** What each click saves on the server. */
const CLICKS = {
  'Reset to default': {
    press: (controls) => controls.reset.onClick(),
    saved: { requiresAuthentication: false, source: 'default', canToggle: true, envVarValue: false }
  },
  'the switch to Public': {
    press: (controls) => controls.toggle.onChange('public'),
    saved: { requiresAuthentication: false, source: 'ui', canToggle: true, envVarValue: false }
  }
};

const mountCard = async () => {
  const server = { security: SECURED_IN_APP };
  const held = { security: [], interval: [], gameLimit: [], saves: [] };
  let holding = false;
  const reconnects = [];
  const handlers = new Map();
  const answer = (body) =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  globalThis.window ??= { location: { origin: 'http://lancache.test' } };
  globalThis.fetch = (url) => {
    const interval = String(url).endsWith('/interval');
    const body = interval ? { interval: 15 } : { gameLimit: 50 };
    if (!holding) return Promise.resolve(answer(body));
    const read = Promise.withResolvers();
    (interval ? held.interval : held.gameLimit).push(() => read.resolve(answer(body)));
    return read.promise;
  };

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
    useSignalR: () => ({
      on: (name, handler) => handlers.set(name, new Set(handlers.get(name)).add(handler)),
      off: (name, handler) => handlers.get(name)?.delete(handler),
      isConnected: true
    }),
    useReconnectRefetch: (_isConnected, refetch) => {
      reconnects.push(refetch);
    },
    useAccordionGroupItem: () => undefined,
    useCopyFeedback: () => [null, () => undefined],
    ApiService: {
      getMetricsSecurity: () => {
        if (!holding) return Promise.resolve(server.security);
        const read = Promise.withResolvers();
        held.security.push(() => read.resolve(server.security));
        return read.promise;
      },
      setMetricsSecurity: () => {
        const save = Promise.withResolvers();
        held.saves.push(save.resolve);
        return save.promise;
      },
      getFetchOptions: (options = {}) => options
    },
    assertOk: async (response) => {
      if (!response.ok) throw new Error(`status ${response.status}`);
    },
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    copyText: async () => true
  });
  const tree = () => {
    reconnects.length = 0;
    return draw(() => drawFeatures(Card({})));
  };
  const controls = () => {
    const drawn = tree();
    const switches = elementsIn(drawn, (element) => element.type === ToggleSwitch);
    const resets = elementsIn(
      drawn,
      (element) =>
        element.type === Button &&
        (element.props.className === 'metrics-reset-button' ||
          element.props.children === RESET_LABEL)
    );
    const sources = elementsIn(
      drawn,
      (element) => element.type === 'p' && element.props.className === 'metrics-source-label'
    );
    assert.equal(switches.length, 1, `the card drew ${switches.length} switches`);
    assert.equal(resets.length, 1, `the card drew ${resets.length} Reset buttons`);
    return {
      toggle: switches[0].props,
      reset: resets[0].props,
      source: sources.map((element) => element.props.children).join(' ')
    };
  };
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      tree();
    }
  };
  return {
    controls,
    settle,
    held,
    hold: () => {
      holding = true;
    },
    // Every reader on the card reads again when the connection comes back.
    reconnect: () => Promise.all(reconnects.map((refetch) => refetch())),
    save: (saved) => {
      server.security = saved;
      for (const handler of handlers.get('MetricsSecurityUpdated') ?? []) handler(saved);
    }
  };
};

test('a reconnect read still out when Reset or the switch saves never puts the old access state back', async () => {
  const shippedFetch = globalThis.fetch;
  const wrong = [];
  try {
    for (const [click, { press, saved }] of Object.entries(CLICKS)) {
      for (const order of ordersOf(['security GET', 'save', 'interval and game-limit GETs'])) {
        const card = await mountCard();
        await card.settle();
        assert.equal(
          card.controls().toggle.value,
          'secured',
          'the card did not load the secured state'
        );

        // The live connection comes back and the card reads again; then the admin clicks.
        card.hold();
        const reading = card.reconnect();
        await card.settle();
        const clicked = press(card.controls());
        await card.settle();
        assert.equal(card.held.saves.length, 1, `${click}: the click started no save`);

        for (const step of order) {
          if (step === 'security GET') card.held.security.splice(0).forEach((open) => open());
          else if (step === 'save') {
            card.save(saved);
            card.held.saves.splice(0).forEach((open) => open(saved));
          } else {
            card.held.interval.splice(0).forEach((open) => open());
            card.held.gameLimit.splice(0).forEach((open) => open());
          }
          await card.settle();
        }
        await reading;
        await clicked;
        await card.settle();

        const shown = card.controls();
        const expectedSource = translate(`management.grafana.metricsToggle.source.${saved.source}`);
        if (shown.toggle.value !== 'public' || shown.source !== expectedSource)
          wrong.push(
            `${click}, answered ${order.join(' then ')}: the card shows ${shown.toggle.value} ("${shown.source}") while the server holds public ("${expectedSource}")`
          );
      }
    }
  } finally {
    globalThis.fetch = shippedFetch;
  }
  assert.deepEqual(
    wrong,
    [],
    `the card shows the access state from before the click: ${wrong.join('; ')}`
  );
});
