import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * Mock mode shows generated data, so the Clients page writes nothing real while it is on: the
 * hostname Save and the exclusions Save are drawn disabled, and their handlers send nothing when
 * called anyway. Both run here as the component ships them, with every name they read bound to a
 * fake that records any write.
 */

const CLIENTS = 'src/components/features/management/sections/ClientsSection.tsx';

/** The `disabled` expression of the one Save button whose `onClick` calls `handler`. */
const saveDisabled = (handler) => {
  const sourceFile = parseSource(CLIENTS, typescript.ScriptKind.TSX);
  const attributesOf = (node) =>
    typescript.isJsxSelfClosingElement(node) || typescript.isJsxOpeningElement(node)
      ? node.attributes.properties.filter(typescript.isJsxAttribute)
      : [];
  const button = findSoleNode(
    sourceFile,
    `Save button calling ${handler}`,
    (node) =>
      (typescript.isJsxSelfClosingElement(node) || typescript.isJsxOpeningElement(node)) &&
      node.tagName.getText(sourceFile) === 'Button' &&
      attributesOf(node).some(
        (attribute) =>
          attribute.name.getText(sourceFile) === 'onClick' &&
          attribute.initializer.getText(sourceFile).includes(handler)
      )
  );
  const disabled = attributesOf(button).find(
    (attribute) => attribute.name.getText(sourceFile) === 'disabled'
  );
  return disabled.initializer.expression.getText(sourceFile);
};

const evaluate = (expression, bindings) => bindLifted(`() => (${expression})`, bindings)();

test('the hostname Save is disabled in mock mode and its handler sends nothing', async () => {
  const shown = {
    savingHostnames: false,
    hostnamesLoading: false,
    hostnamesChanged: true
  };
  assert.equal(
    evaluate(saveDisabled('handleHostnamesSave'), { ...shown, mockMode: false }),
    false,
    'the hostname Save is disabled with a change waiting and mock mode off'
  );
  assert.equal(
    evaluate(saveDisabled('handleHostnamesSave'), { ...shown, mockMode: true }),
    true,
    'the hostname Save can be clicked in mock mode'
  );

  const writes = [];
  const save = bindLifted(liftHookCallback(CLIENTS, 'useCallback', 'setHostnameSettings({'), {
    mockMode: true,
    savingHostnamesRef: { current: false },
    setSavingHostnames: () => undefined,
    setHostnameSettings: async (settings) => {
      writes.push({ settings });
    },
    setHostnamesEnabled: async (enabled) => {
      writes.push({ enabled });
    },
    hostnameForm: {
      enabled: true,
      guestAccess: false,
      routerLookup: true,
      dockerLookup: true,
      resolver: '10.0.0.53'
    },
    hostnamesEnabled: false,
    onSuccess: () => undefined,
    onError: () => undefined,
    t: (key) => key
  });
  await save();
  assert.deepEqual(writes, [], `the mock-mode hostname Save sent ${JSON.stringify(writes)}`);
});

test('the exclusions Save is disabled in mock mode and its handler sends nothing', async () => {
  const shown = {
    hasExcludedChanges: true,
    savingExcluded: false,
    loadingExcluded: false
  };
  assert.equal(
    evaluate(saveDisabled('handleSaveExcluded'), { ...shown, mockMode: false }),
    false,
    'the exclusions Save is disabled with a change waiting and mock mode off'
  );
  assert.equal(
    evaluate(saveDisabled('handleSaveExcluded'), { ...shown, mockMode: true }),
    true,
    'the exclusions Save can be clicked in mock mode'
  );

  const writes = [];
  const rules = [{ ip: '192.168.1.40', mode: 'exclude', traffic: 'all', services: [] }];
  const save = bindLifted(liftConstArrow(CLIENTS, 'handleSaveExcluded'), {
    mockMode: true,
    savingExcludedRef: { current: false },
    savesStartedRef: { current: 0 },
    setSavingExcluded: () => undefined,
    excludedRules: rules,
    ruleKey: (rule) => rule.ip,
    ApiService: {
      updateStatsExclusionRules: async (sent) => {
        writes.push(sent);
        return { rules: sent };
      }
    },
    setSavedExcludedRules: () => undefined,
    serializeRules: (list) => JSON.stringify(list),
    excludedRulesRef: { current: rules },
    setExcludedRules: () => undefined,
    assignRowIds: (list) => list,
    nextRowIdRef: { current: 0 },
    onSuccess: () => undefined,
    onError: () => undefined,
    t: (key) => key,
    refreshStats: async () => undefined,
    refreshDownloads: async () => undefined,
    reloadExclusions: () => undefined
  });
  await save();
  assert.deepEqual(writes, [], `the mock-mode exclusions Save sent ${JSON.stringify(writes)}`);
});
