import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import { translator } from './transpile-module.mjs';

/**
 * An admin clicks Secured on the `/metrics` switch. The server saves Secured and pushes
 * `MetricsSecurityUpdated`, but the reply to the admin's own save is lost and the save rejects. The
 * push and the failed reply can reach the page in either order, and in both the switch must end on
 * Secured, the rule the server holds; it must never show Public while `/metrics` asks for the key.
 * Both places that draw the switch run: the Grafana card and the Users page sign-in pane. Each runs
 * on a hook store with the feature components it draws; the save is held until the test fails it.
 */

const HOSTS = {
  'the Grafana card': {
    file: 'src/components/features/management/grafana/GrafanaEndpoints.tsx',
    name: 'GrafanaEndpoints'
  },
  'the sign-in pane': {
    file: 'src/components/features/user/MetricsAccessRow.tsx',
    name: 'MetricsAccessRow'
  }
};
const ORDERS = {
  'the push lands before the failed reply': ['push', 'fail'],
  'the failed reply lands before the push': ['fail', 'push']
};
const translate = translator('en');
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);

/** A server answer for the access state. */
const access = (requiresAuthentication, source) => ({
  requiresAuthentication,
  source,
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

const mountHost = async ({ file, name }) => {
  const server = { security: access(false, 'default') };
  const saves = [];
  const handlers = new Map();
  const answer = (body) =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  globalThis.window ??= { location: { origin: 'http://lancache.test' } };
  globalThis.fetch = async (url) =>
    answer(String(url).endsWith('/interval') ? { interval: 15 } : { gameLimit: 50 });

  const { hooks, draw } = hookStore();
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const { Root, draw: drawFeatures } = await featureTree(file, name, {
    React: { ...React, ...hooks },
    ...hooks,
    ToggleSwitch,
    Button,
    EnhancedDropdown: inert('EnhancedDropdown'),
    AccordionSection: passthrough,
    UserSettingRow: passthrough,
    useTranslation: () => ({ t: translate }),
    useAuth: () => ({ isAdmin: true }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSignalR: () => ({
      on: (event, handler) => handlers.set(event, handler),
      off: (event) => handlers.delete(event),
      isConnected: true
    }),
    useReconnectRefetch: () => undefined,
    useAccordionGroupItem: () => undefined,
    useCopyFeedback: () => [null, () => undefined],
    ApiService: {
      getMetricsSecurity: async () => ({ ...server.security }),
      setMetricsSecurity: (enabled) =>
        new Promise((resolve, reject) => saves.push({ enabled, resolve, reject })),
      getFetchOptions: (options = {}) => options
    },
    assertOk: async (response) => {
      if (!response.ok) throw new Error(`status ${response.status}`);
    },
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    copyText: async () => true
  });
  const tree = () => draw(() => drawFeatures(Root({})));
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      tree();
    }
  };
  const toggle = () => {
    const switches = elementsIn(tree(), (element) => element.type === ToggleSwitch);
    assert.equal(switches.length, 1, `${name} drew ${switches.length} switches`);
    return switches[0].props;
  };
  return {
    server,
    saves,
    settle,
    toggle,
    push: (pushed) => handlers.get('MetricsSecurityUpdated')(pushed)
  };
};

test('a failed Secured save never shows Public over the Secured the server pushed, in either order', async () => {
  const shippedFetch = globalThis.fetch;
  const wrong = [];
  try {
    for (const [hostName, host] of Object.entries(HOSTS)) {
      for (const [orderName, order] of Object.entries(ORDERS)) {
        const page = await mountHost(host);
        await page.settle();
        assert.equal(page.toggle().value, 'public', `${hostName} did not load the public state`);

        const clicked = Promise.resolve(page.toggle().onChange('secured')).catch(() => undefined);
        await page.settle();
        assert.equal(
          page.saves.length,
          1,
          `${hostName}: the click sent ${page.saves.length} saves`
        );

        // The server saved Secured; its push and the lost reply now reach the page.
        page.server.security = access(true, 'ui');
        for (const step of order) {
          if (step === 'push') page.push(access(true, 'ui'));
          else page.saves[0].reject(new Error('the reply was lost after the server saved'));
          await page.settle();
        }
        await clicked;
        await page.settle();

        const shown = page.toggle().value;
        if (shown !== 'secured')
          wrong.push(
            `${hostName}, ${orderName}: the switch shows ${shown} while the server holds secured`
          );
      }
    }
  } finally {
    globalThis.fetch = shippedFetch;
  }
  assert.deepEqual(wrong, [], wrong.join('; '));
});
