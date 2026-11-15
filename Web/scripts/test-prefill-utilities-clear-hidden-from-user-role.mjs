import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import typescript from 'typescript';
import { hookStore } from './component-hooks.mjs';
import { featureTree } from './component-tree.mjs';
import {
  bindLifted,
  compileTree,
  findSoleNode,
  moduleUrl,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * Clearing the prefill cache database changes every account's and guest's prefill, so the server
 * refuses it to a user-role account. The prefill panel's Utilities menu therefore offers it to an
 * admin and not to a user-role account. The command panel is drawn here with the props the shipped
 * prefill panel hands it, read out of that panel, once for each role.
 */

const translate = translator('en');
const panelPath = 'src/components/features/prefill/PrefillPanel.tsx';

// The command catalog reads the app's i18n instance while it loads; the locale lookup stands in.
globalThis.prefillUtilitiesTranslate = translate;
const commandCatalog = await import(
  await compileTree('../src/components/features/prefill/types.ts', {
    '@/i18n': moduleUrl('export default { t: (key) => globalThis.prefillUtilitiesTranslate(key) };')
  })
);

/** What the prefill panel holds for a signed-in account of `sessionType` on a ready Steam session. */
const panelState = (sessionType) => ({
  isAdmin: true,
  authMode: 'authenticated',
  sessionType,
  isReadyForCommands: true,
  isExecuting: false,
  signalR: { isPrefillActive: false, runs: [], canStart: true, session: null },
  isPrefillRunActive: () => false,
  supportsConcurrentPrefill: () => false,
  isSessionActive: true,
  selectedAppIds: [],
  selectedOS: [],
  maxConcurrency: 'auto',
  maxThreadLimit: null,
  serviceConfig: { prefillCommands: ['prefill'], supportedOperatingSystems: [] },
  cachedAppIds: [],
  estimatedSize: { bytes: 0, loading: false },
  handleCommandClick: () => undefined,
  handleOSChange: () => undefined,
  handleConcurrencyChange: () => undefined
});

/**
 * Evaluates `source` with `state` bound. A name the state does not hold is read from the panel's own
 * `const name = ...` declaration, so a value the panel computes from the role is computed the way it
 * ships.
 */
const evaluate = (panel, source, state) => {
  const bindings = { ...state };
  for (;;) {
    try {
      return bindLifted(`() => (${source})`, bindings)();
    } catch (error) {
      const missing =
        error instanceof ReferenceError && /^(\w+) is not defined$/.exec(error.message);
      if (!missing) throw error;
      const name = missing[1];
      const declaration = findSoleNode(
        panel,
        `${name} declaration`,
        (node) =>
          typescript.isVariableDeclaration(node) &&
          typescript.isIdentifier(node.name) &&
          node.name.text === name &&
          node.initializer !== undefined
      );
      bindings[name] = evaluate(panel, declaration.initializer.getText(panel), state);
    }
  }
};

/** The props the shipped panel passes to its command buttons, for an account of `sessionType`. */
const commandButtonProps = (sessionType) => {
  const panel = parseSource(panelPath);
  const element = findSoleNode(
    panel,
    'PrefillCommandButtons element',
    (node) =>
      typescript.isJsxOpeningLikeElement(node) &&
      node.tagName.getText(panel) === 'PrefillCommandButtons'
  );
  const state = panelState(sessionType);
  const props = {};
  for (const attribute of element.attributes.properties) {
    if (typescript.isJsxSpreadAttribute(attribute)) {
      Object.assign(props, evaluate(panel, attribute.expression.getText(panel), state));
    } else if (!attribute.initializer) {
      props[attribute.name.getText(panel)] = true;
    } else if (typescript.isStringLiteral(attribute.initializer)) {
      props[attribute.name.getText(panel)] = attribute.initializer.text;
    } else {
      props[attribute.name.getText(panel)] = evaluate(
        panel,
        attribute.initializer.expression.getText(panel),
        state
      );
    }
  }
  return props;
};

/** Every string the drawn tree shows, with each menu's render-function children opened. */
const screenText = (node) => {
  if (Array.isArray(node)) return node.flatMap(screenText);
  if (typeof node === 'string') return [node];
  if (!React.isValidElement(node)) return [];
  return Object.entries(node.props).flatMap(([name, value]) =>
    name === 'children' && typeof value === 'function'
      ? screenText(value(() => undefined))
      : screenText(value)
  );
};

test('the prefill Utilities menu offers Clear Database to an admin and not to a user-role account', async () => {
  const shown = async (sessionType) => {
    const { hooks, draw } = hookStore();
    const { Root: PrefillCommandButtons, draw: drawFeatures } = await featureTree(
      'src/components/features/prefill/PrefillCommandButtons.tsx',
      'PrefillCommandButtons',
      {
        React: { ...React, ...hooks },
        ...hooks,
        ...commandCatalog,
        useTranslation: () => ({ t: translate }),
        formatBytes: (bytes) => `${bytes} B`
      }
    );
    const props = commandButtonProps(sessionType);
    return screenText(draw(() => drawFeatures(PrefillCommandButtons(props))));
  };

  const clearLabel = translate('prefill.commands.clear-cache-data.label');
  assert.ok(
    (await shown('admin')).includes(clearLabel),
    `an admin is no longer offered ${clearLabel} in the Utilities menu`
  );
  assert.ok(
    !(await shown('user')).includes(clearLabel),
    `a user-role account is offered ${clearLabel} in the prefill Utilities menu, and the server refuses every click`
  );
});
