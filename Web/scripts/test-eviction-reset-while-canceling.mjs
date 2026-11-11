import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  findSoleNode,
  loadNotificationModules,
  moduleUrl,
  operationBusyFor,
  operationRunRow,
  parseSource,
  pushRun
} from './transpile-module.mjs';

/**
 * The server refuses Reset evictions with a busy conflict for as long as an eviction removal is
 * active, and a removal the user stopped stays active until it has wound down. So the Reset
 * evictions menu item has to stay disabled while that removal's card reads Canceling, or the click
 * it offers only earns a "reset failed" error.
 *
 * The run list comes from the real run store, the busy flags from the real `useOperationBusy` with
 * the options the page ships, and the item's `disabled` expression is read out of the page and
 * evaluated with every name in it resolved from the page's own declarations.
 */

const modules = await loadNotificationModules(
  moduleUrl('export default { t: (key) => key, exists: () => true };')
);
const { createRunStoreState, deriveRuns, setRunCancel } = modules;

// The run list the page's busy flags read, set by `resetDisabled` before each evaluation.
let pageRuns = [];

const STORAGE = 'src/components/features/management/sections/StorageSection.tsx';
const source = parseSource(STORAGE, typescript.ScriptKind.TSX);

/** The `disabled` expression of the menu item labeled Reset evictions, as the page ships it. */
const resetItem = findSoleNode(
  source,
  'Reset evictions menu item',
  (node) =>
    typescript.isJsxElement(node) &&
    node.openingElement.attributes.properties.some(
      (attribute) =>
        typescript.isJsxAttribute(attribute) && attribute.name.getText(source) === 'disabled'
    ) &&
    node.children.some((child) =>
      child.getText(source).includes("'management.sections.data.resetEvictions'")
    )
);
const disabledExpression = resetItem.openingElement.attributes.properties.find(
  (attribute) =>
    typescript.isJsxAttribute(attribute) && attribute.name.getText(source) === 'disabled'
).initializer.expression;

/** Every plain name an expression reads, property names left out. */
const namesIn = (expression) =>
  [
    ...new Set(
      [expression, ...collectNodes(expression, () => true)]
        .filter(
          (node) =>
            typescript.isIdentifier(node) &&
            !(typescript.isPropertyAccessExpression(node.parent) && node.parent.name === node)
        )
        .map((node) => node.getText(source))
    )
  ].sort();

/**
 * The value `name` holds in the page while nothing is being clicked: a `useState` flag holds its
 * initial value, a `useOperationBusy` flag is what the real hook says for the run list, and any
 * other `const` is its initializer worked out the same way.
 */
const valueOf = (name) => {
  const stateFlag = collectNodes(
    source,
    (node) =>
      typescript.isBindingElement(node) &&
      typescript.isArrayBindingPattern(node.parent) &&
      node.name.getText(source) === name
  );
  if (stateFlag.length === 1) {
    const call = stateFlag[0].parent.parent.initializer;
    assert.equal(call.expression.getText(source), 'useState', `${name} is not a useState value`);
    return evaluate(call.arguments[0]);
  }
  const declaration = findSoleNode(
    source,
    `${name} declaration`,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      typescript.isIdentifier(node.name) &&
      node.name.getText(source) === name
  );
  const initializer = declaration.initializer;
  if (
    typescript.isCallExpression(initializer) &&
    initializer.expression.getText(source) === 'useOperationBusy'
  ) {
    const options = bindLifted(`() => (${initializer.arguments[0].getText(source)})`, {})();
    return operationBusyFor(options, pageRuns);
  }
  return evaluate(initializer);
};

const evaluate = (expression) => {
  const bindings = Object.fromEntries(namesIn(expression).map((name) => [name, valueOf(name)]));
  return bindLifted(`() => (${expression.getText(source)})`, bindings)();
};

/** Whether Reset evictions is disabled for the run list the store derives from `state`. */
const resetDisabled = (state) => {
  pageRuns = deriveRuns(state);
  return Boolean(evaluate(disabledExpression));
};

const removalRow = (status) =>
  operationRunRow('R1', {
    operationType: 'evictionRemoval',
    name: 'Eviction Removal',
    message: 'signalr.evictionRemoval.removing',
    status
  });

/**
 * The two ways a removal card comes to read Canceling: the server's row says the run is canceling,
 * or this user pressed the card's cancel and the server has not answered yet.
 */
const cancelOrders = [
  {
    label: 'the server reports the removal as canceling',
    cancel: (state) => pushRun(modules, state, removalRow('canceling'))
  },
  {
    label: 'this user pressed cancel on the running removal',
    cancel: (state) =>
      setRunCancel(state, deriveRuns(state)[0].id, { cancelRequested: true, canceling: true })
  }
];

test('Reset evictions stays disabled while an eviction removal is canceling', () => {
  const running = pushRun(modules, createRunStoreState(), removalRow('running'));
  assert.equal(
    resetDisabled(running),
    true,
    'Reset evictions is enabled while an eviction removal runs'
  );

  const enabledWhileCanceling = cancelOrders
    .filter(({ label, cancel }) => {
      const canceling = cancel(running);
      assert.equal(deriveRuns(canceling)[0].status, 'canceling', `${label}: card is not Canceling`);
      return !resetDisabled(canceling);
    })
    .map(({ label }) => label);
  assert.deepEqual(
    enabledWhileCanceling,
    [],
    `Reset evictions is enabled while the removal card reads Canceling (${enabledWhileCanceling.join('; ')}), so the click gets the server's busy refusal (errors.conflict.evictionResetBusy)`
  );

  const ended = pushRun(modules, running, removalRow('canceled'));
  assert.equal(
    resetDisabled(ended),
    false,
    'Reset evictions stays disabled after the removal has ended'
  );
});
