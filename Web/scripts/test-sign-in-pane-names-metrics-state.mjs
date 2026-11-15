import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import typescript from 'typescript';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { compileTree, transpile } from './transpile-module.mjs';

/**
 * `/metrics` answers with no key unless the admin requires one on the Grafana card, and that stays
 * the default. So while sign-in is on, the Users page sign-in pane says which of the two states
 * `/metrics` is in: a pane that reads the same whether the endpoint is public or needs the key tells
 * an admin who just locked the app down nothing about the one route still open.
 *
 * The shipped pane and every component it imports from its own folder are run on a small renderer
 * over the hook store, each with its own hooks, with the rest of the app stubbed at the edges, and
 * the text it draws is read once each answer has landed. Translation hands back the key, so the text
 * shows which strings were chosen. `SIGN_IN_PANE_FOLDER` points the test at another copy of
 * the pane's folder.
 */

const folder =
  process.env.SIGN_IN_PANE_FOLDER ||
  fileURLToPath(new URL('../src/components/features/user/', import.meta.url));

/** The current component's hooks, so every component drawn keeps its own. */
let current = null;
const hook =
  (name) =>
  (...args) =>
    current.hooks[name](...args);
const hooks = Object.fromEntries(
  ['useState', 'useRef', 'useMemo', 'useCallback', 'useEffect', 'useLayoutEffect'].map((name) => [
    name,
    hook(name)
  ])
);

/** Draws nothing. */
const inert = () => null;

/** The shipped key table the row reads its source line from. */
const { METRICS_SOURCE_KEYS } = await import(
  await compileTree('../src/components/features/management/grafana/metricsSourceKeys.ts')
);

/** The Grafana card's switch, drawn as the state the row handed it. */
const switchAtState = ({ security }) =>
  security.requiresAuthentication ? 'switch at secured' : 'switch at public';

/**
 * A component file from the folder, compiled with its imports dropped. Each import from the same
 * folder is loaded the same way; every other import is taken from `edges`, or drawn as nothing.
 */
const load = (file, name, edges, loaded = new Map()) => {
  const key = `${file}#${name}`;
  if (loaded.has(key)) return loaded.get(key);
  const source = typescript.createSourceFile(
    file,
    readFileSync(`${folder}${file}`, 'utf8'),
    typescript.ScriptTarget.Latest,
    true,
    typescript.ScriptKind.TSX
  );
  const bindings = { React: { ...React, ...hooks }, ...hooks };
  for (const declaration of source.statements.filter(typescript.isImportDeclaration)) {
    const clause = declaration.importClause;
    if (!clause || clause.isTypeOnly) continue;
    const from = declaration.moduleSpecifier.text;
    const sibling = from.startsWith('./') ? `${from.slice(2)}.tsx` : null;
    const names = [
      ...(clause.name ? [[clause.name.text, 'default']] : []),
      ...(clause.namedBindings && typescript.isNamedImports(clause.namedBindings)
        ? clause.namedBindings.elements
            .filter((element) => !element.isTypeOnly)
            .map((element) => [element.name.text, element.name.text])
        : [])
    ];
    for (const [local] of names) {
      if (local in bindings) continue;
      bindings[local] =
        sibling && existsSync(`${folder}${sibling}`)
          ? load(sibling, local, edges, loaded)
          : local in edges
            ? edges[local]
            : inert;
    }
  }
  const body = source.statements
    .filter((node) => !typescript.isImportDeclaration(node) && !typescript.isExportAssignment(node))
    .map((node) => node.getText(source))
    .join('\n')
    .replace(/\bexport\s+/g, '');
  const code = transpile(body, typescript.ModuleKind.CommonJS, { jsx: typescript.JsxEmit.React });
  const names = Object.keys(bindings).filter((binding) => binding !== name);
  const component = new Function(...names, `${code}\nreturn ${name};`)(
    ...names.map((binding) => bindings[binding])
  );
  loaded.set(key, component);
  return component;
};

/** The text a tree draws, running each function component on its own hook store. */
const textOf = (node, stores, path) => {
  if (node === null || node === undefined || typeof node === 'boolean') return '';
  if (typeof node === 'string' || typeof node === 'number') return `${node} `;
  if (Array.isArray(node))
    return node.map((child, index) => textOf(child, stores, `${path}.${index}`)).join('');
  if (!React.isValidElement(node)) return '';
  if (typeof node.type === 'function') {
    const at = `${path}/${node.type.name}`;
    if (!stores.has(at)) stores.set(at, hookStore());
    const store = stores.get(at);
    current = store;
    const drawn = store.draw(() => node.type(node.props));
    return textOf(drawn, stores, at);
  }
  return textOf(node.props.children, stores, `${path}>`);
};

/** What the sign-in pane draws for an owner with sign-in on, once every read has answered. */
const paneText = async (requiresAuthentication) => {
  const security = {
    requiresAuthentication,
    source: 'default',
    canToggle: true,
    envVarValue: false
  };
  const shippedFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(JSON.stringify(security), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  try {
    const answer = async () => ({ ...security });
    const edges = {
      useTranslation: () => ({ t: (key) => key }),
      useAuth: () => ({
        isMainAdmin: true,
        authenticationEnabled: true,
        accountMode: 'apiKeyPassword',
        signInModeFromEnvironment: false,
        loginServices: [],
        refreshAuth: async () => undefined
      }),
      useErrorHandler: () => ({ notifyError: () => undefined }),
      useHeldValue: (value) => value,
      useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
      useReconnectRefetch: () => undefined,
      ApiService: new Proxy({}, { get: () => answer }),
      authService: new Proxy({}, { get: () => answer }),
      getErrorMessage: (error) => error.message,
      isAbortError: () => false,
      usesOidc: () => false,
      API_BASE: '/api',
      METRICS_SOURCE_KEYS,
      MetricsAccessSwitch: switchAtState
    };
    const SignInPane = load('SignInPane.tsx', 'SignInPane', edges);
    const stores = new Map();
    let text = '';
    for (let pass = 0; pass < 8; pass += 1) {
      text = textOf(React.createElement(SignInPane), stores, 'root');
      await turn();
    }
    return text.replace(/\s+/g, ' ').trim();
  } finally {
    globalThis.fetch = shippedFetch;
  }
};

test('with sign-in on, the sign-in pane shows whether /metrics needs the API key', async () => {
  const open = await paneText(false);
  const locked = await paneText(true);
  assert.notEqual(
    open,
    locked,
    `the sign-in pane reads the same whether /metrics is public or needs the API key: "${open}"`
  );
});
