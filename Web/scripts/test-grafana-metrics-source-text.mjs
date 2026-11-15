import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { compileTree, make, parseSource } from './transpile-module.mjs';

/**
 * The Grafana card's Endpoint Access row says where the metrics sign-in rule comes from: a UI
 * override, the config file or an environment variable, or the built-in default. The server's
 * answer names the origin in `source`, so the line shown must follow `source`: a rule the config
 * sets must not read as the default because the config happens to name the same value.
 *
 * The whole shipped card runs here on a small hook store with a translator that reads the shipped
 * en.json. Effects run once after the first draw, so the card loads its settings from stubbed
 * replies; the test then reads the text drawn beside the Endpoint Access label.
 */

const COMPONENT = 'src/components/features/management/grafana/GrafanaEndpoints.tsx';
/** The shipped key table the card reads the source line from. */
const { METRICS_SOURCE_KEYS } = await import(
  await compileTree('../src/components/features/management/grafana/metricsSourceKeys.ts')
);

const english = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);
const translate = (key) => {
  const text = key.split('.').reduce((node, part) => node?.[part], english);
  return typeof text === 'string' ? text : `no en text for ${key}`;
};

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the card imports, so a stub stands in for any the test does not bind itself. */
const importedNames = parseSource(COMPONENT)
  .statements.filter(typescript.isImportDeclaration)
  .flatMap((declaration) => {
    const clause = declaration.importClause;
    if (!clause || clause.isTypeOnly) return [];
    const names = clause.name ? [clause.name.text] : [];
    const bindings = clause.namedBindings;
    if (bindings && typescript.isNamedImports(bindings))
      names.push(
        ...bindings.elements
          .filter((element) => !element.isTypeOnly)
          .map((element) => element.name.text)
      );
    return names;
  });

/** Lets every handler continuation that is already due run. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

/** The text `node` draws as its children. */
const textOf = (node) => {
  if (Array.isArray(node)) return node.map(textOf).join(' ');
  if (typeof node === 'string') return node;
  if (React.isValidElement(node)) return textOf(node.props.children);
  return '';
};

/** The line drawn beside the Endpoint Access label once the card loaded `security`. */
const endpointAccessSource = async (security) => {
  const slots = [];
  const effects = [];
  let cursor = 0;
  let firstDraw = true;
  const hooks = {
    useState: (initial) => {
      const index = cursor++;
      const slot = (slots[index] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => {
      const index = cursor++;
      return (slots[index] ??= { current: initial });
    },
    useMemo: (build) => {
      const index = cursor++;
      return (slots[index] ??= { value: build() }).value;
    },
    useCallback: (callback) => {
      const index = cursor++;
      return (slots[index] ??= { value: callback }).value;
    },
    useEffect: (effect) => {
      cursor++;
      if (firstDraw) effects.push(effect);
    },
    useLayoutEffect: (effect) => {
      cursor++;
      if (firstDraw) effects.push(effect);
    }
  };
  const Card = make(COMPONENT, 'GrafanaEndpoints', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    METRICS_SOURCE_KEYS,
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: translate }),
    useAuth: () => ({ isAdmin: true }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    useAccordionGroupItem: () => undefined,
    useCopyFeedback: (initial) => [initial, () => undefined],
    ApiService: {
      getMetricsSecurity: async () => security,
      setMetricsSecurity: async () => security,
      getFetchOptions: (options) => ({ ...options })
    },
    assertOk: async (response) => response,
    getErrorMessage: (error) => String(error),
    isAbortError: () => false,
    copyText: async () => true
  });

  globalThis.window = { location: { origin: 'http://lancache.test' } };
  globalThis.fetch = async (url) => ({
    ok: true,
    status: 200,
    json: async () => (String(url).includes('game-limit') ? { gameLimit: 50 } : { interval: 15 })
  });
  try {
    cursor = 0;
    Card({});
    firstDraw = false;
    effects.forEach((effect) => effect());
    await settle();
    await settle();
    cursor = 0;
    const tree = Card({});

    const label = translate('management.grafana.accessMode');
    const found = [];
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (!React.isValidElement(node)) return;
      const children = React.Children.toArray(node.props.children);
      if (children.some((child) => React.isValidElement(child) && textOf(child) === label))
        found.push(children.filter((child) => textOf(child) !== label));
      Object.values(node.props).forEach(walk);
    };
    walk(tree);
    assert.equal(found.length, 1, 'the card does not draw one Endpoint Access label');
    return textOf(found[0]).trim();
  } finally {
    delete globalThis.fetch;
    delete globalThis.window;
  }
};

const answers = {
  configTrue: {
    requiresAuthentication: true,
    source: 'config',
    canToggle: true,
    envVarValue: true
  },
  configFalse: {
    requiresAuthentication: false,
    source: 'config',
    canToggle: true,
    envVarValue: false
  },
  builtIn: {
    requiresAuthentication: false,
    source: 'default',
    canToggle: true,
    envVarValue: false
  },
  override: { requiresAuthentication: true, source: 'ui', canToggle: true, envVarValue: false }
};

test('the Endpoint Access source line follows where the metrics rule comes from', async () => {
  const shown = {};
  for (const [name, answer] of Object.entries(answers))
    shown[name] = await endpointAccessSource(answer);

  for (const text of Object.values(shown)) assert.doesNotMatch(text, /no en text for/, text);
  assert.match(
    shown.configTrue,
    /config file|environment variable/i,
    `a metrics rule the config sets to "secured" (source "config") shows "${shown.configTrue}"`
  );
  assert.match(
    shown.configFalse,
    /config file|environment variable/i,
    `a metrics rule the config sets to the built-in "public" (source "config") shows "${shown.configFalse}"`
  );
  assert.match(
    shown.builtIn,
    /default/i,
    `the built-in metrics rule (source "default") shows "${shown.builtIn}"`
  );
  assert.notEqual(
    shown.builtIn,
    shown.configFalse,
    `the built-in metrics rule and one the config sets both show "${shown.builtIn}"`
  );
  assert.ok(
    shown.override !== shown.builtIn && shown.override !== shown.configTrue,
    `a metrics rule set here (source "ui") shows "${shown.override}", the same as another origin`
  );
});
