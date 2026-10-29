import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  loadNotificationModules,
  moduleUrl,
  operationRunRow,
  parseSource,
  pushRun
} from './transpile-module.mjs';

/**
 * Remove all is disabled while the cache or log folder is missing or read-only. The menu item says
 * which folder, so the reason reaches the tooltip and keyboard focus through the gate.
 */

const corruptionPath = 'src/components/features/management/cache/CorruptionManager.tsx';

const sourceFile = parseSource(corruptionPath, typescript.ScriptKind.TSX);

const removeAllReason = (overrides = {}) => {
  const declaration = findSoleNode(
    sourceFile,
    'removeAllReason declaration',
    (node) =>
      typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === 'removeAllReason'
  ).getText(sourceFile);
  return bindLifted(`() => (${declaration.slice(declaration.indexOf('=') + 1)})`, {
    mockMode: false,
    isCorruptionRemovalActive: false,
    startingRemoveAll: false,
    removeAllPostPending: false,
    anyServiceRemovalPending: false,
    startingRemoveSelected: false,
    checkingPermissions: false,
    directoryMissing: false,
    isReadOnly: false,
    cacheExist: true,
    cacheReadOnly: false,
    isRefreshing: false,
    diskObjectsAvailable: true,
    diskObjectReason: '',
    nginxReopenAvailable: true,
    nginxReopenUnavailableMessage: '',
    scanId: 'scan-1',
    projection: { total: 4 },
    t: (key) => key,
    ...overrides
  })();
};

test('Remove all names the missing or read-only folder that disables it', () => {
  assert.equal(
    removeAllReason({ directoryMissing: true, cacheExist: false }),
    'management.directoryNotice.cacheMissingTitle'
  );
  assert.equal(
    removeAllReason({ directoryMissing: true }),
    'management.directoryNotice.logsMissingTitle'
  );
  assert.equal(
    removeAllReason({ isReadOnly: true, cacheReadOnly: true }),
    'management.directoryNotice.cacheReadOnlyTitle'
  );
  assert.equal(
    removeAllReason({ isReadOnly: true }),
    'management.directoryNotice.logsReadOnlyTitle'
  );
  // The permission check still runs, so its own reason comes first.
  assert.equal(
    removeAllReason({ checkingPermissions: true, directoryMissing: true, cacheExist: false }),
    'management.corruption.checkingPermissions'
  );
  assert.equal(removeAllReason(), '');
});

const mockReason = 'management.sections.settings.mockModeActive';

test('Remove all says Demo Mode first, ahead of every other reason', () => {
  assert.equal(removeAllReason({ mockMode: true }), mockReason);
  assert.equal(
    removeAllReason({ mockMode: true, startingRemoveAll: true, directoryMissing: true }),
    mockReason
  );
});

const scanReason = (overrides = {}) => {
  const lifted = (name, bindings) => {
    const declaration = findSoleNode(
      sourceFile,
      `${name} declaration`,
      (node) => typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
    ).getText(sourceFile);
    return bindLifted(`() => (${declaration.slice(declaration.indexOf('=') + 1)})`, bindings)();
  };
  const bindings = {
    mockMode: false,
    isScanBusy: false,
    corruptionRemovalBusy: false,
    isAnyRemovalRunning: false,
    isRefreshing: false,
    checkingPermissions: false,
    directoryMissing: false,
    cacheExist: true,
    diskObjectsAvailable: true,
    diskObjectReason: '',
    scanGate: { available: true, tooltip: '' },
    t: (key, options) => (options ? `${key}:${options.operation}` : key),
    ...overrides
  };
  return lifted('scanReason', { ...bindings, loadReason: lifted('loadReason', bindings) });
};

test('every state that blocks a scan gives the Scan items a reason', () => {
  const active = 'management.corruption.removalActive';
  assert.equal(scanReason({ mockMode: true, isScanBusy: true }), mockReason);
  assert.equal(
    scanReason({ isScanBusy: true }),
    `${active}:common.notifications.titles.corruptionDetection`
  );
  assert.equal(
    scanReason({ corruptionRemovalBusy: true }),
    `${active}:common.notifications.titles.corruptionRemoval`
  );
  assert.equal(
    scanReason({ isAnyRemovalRunning: true }),
    'management.corruption.cacheRemovalRunning'
  );
  assert.equal(scanReason({ isRefreshing: true }), 'management.corruption.loadingCachedData');
  assert.equal(
    scanReason({ checkingPermissions: true }),
    'management.corruption.checkingPermissions'
  );
  assert.equal(
    scanReason({ directoryMissing: true, cacheExist: false }),
    'management.directoryNotice.cacheMissingTitle'
  );
  assert.equal(
    scanReason({ directoryMissing: true }),
    'management.directoryNotice.logsMissingTitle'
  );
  assert.equal(
    scanReason({ scanGate: { available: false, tooltip: 'A download is running' } }),
    'A download is running'
  );
  assert.equal(scanReason(), '');
});

test('each Scan item and Remove selected sit in a reason gate that matches their disabled state', () => {
  const items = [];
  const visit = (node) => {
    if (
      typescript.isJsxElement(node) &&
      typescript.isJsxOpeningElement(node.openingElement) &&
      ['ActionMenuItem', 'ActionMenuDangerItem'].includes(
        node.openingElement.tagName.getText(sourceFile)
      )
    ) {
      items.push(node);
    }
    typescript.forEachChild(node, visit);
  };
  visit(sourceFile);
  const attribute = (opening, name) =>
    opening.attributes.properties
      .find(
        (property) =>
          typescript.isJsxAttribute(property) && property.name.getText(sourceFile) === name
      )
      ?.initializer?.expression?.getText(sourceFile)
      .replace(/\s+/g, ' ');
  const enclosingGate = (item) => {
    for (let node = item.parent; node; node = node.parent) {
      if (
        typescript.isJsxElement(node) &&
        node.openingElement.tagName.getText(sourceFile) === 'DisabledActionGate'
      ) {
        return node.openingElement;
      }
    }
    return null;
  };
  const gated = (labelKey) => {
    const matches = items.filter((item) => item.getText(sourceFile).includes(labelKey));
    assert.ok(matches.length > 0, labelKey);
    return matches.map((item) => ({ item: item.openingElement, gate: enclosingGate(item) }));
  };
  for (const key of [
    'management.corruption.fullScan',
    'management.corruption.incrementalScan',
    'common.scan'
  ]) {
    for (const { item, gate } of gated(key)) {
      assert.ok(gate, `${key} has no reason gate`);
      assert.equal(attribute(gate, 'reason'), 'scanReason', key);
      assert.equal(attribute(gate, 'disabled'), attribute(item, 'disabled'), key);
    }
  }
  for (const { item, gate } of gated('removeSelectedLabel')) {
    assert.ok(gate, 'Remove selected has no reason gate');
    assert.equal(attribute(gate, 'reason'), 'removeSelectedReason');
    assert.equal(attribute(gate, 'disabled'), attribute(item, 'disabled'));
  }
});

test('the Remove all gate carries that reason', () => {
  const gate = findSoleNode(
    sourceFile,
    'DisabledActionGate labelled removeAllLabel',
    (node) =>
      (typescript.isJsxOpeningElement(node) || typescript.isJsxSelfClosingElement(node)) &&
      node.tagName.getText(sourceFile) === 'DisabledActionGate' &&
      node.attributes.getText(sourceFile).includes('label={removeAllLabel}')
  );
  assert.match(gate.attributes.getText(sourceFile), /reason=\{removeAllReason\}/);
});

test('a disabled Remove all with a folder reason is a keyboard stop described by it', async () => {
  const react = import.meta.resolve('react');
  const tooltipUrl = moduleUrl(
    `import React from '${react}';\n` +
      'export const Tooltip = ({ content, className, children }) =>\n' +
      "  React.createElement('div', { 'data-tooltip': content, className }, children);"
  );
  const { DisabledActionGate } = await import(
    await compileToUrl(
      '../src/components/features/management/DisabledActionGate.tsx',
      { react, '@components/ui/Tooltip': tooltipUrl },
      { jsx: typescript.JsxEmit.React }
    )
  );
  const reason = removeAllReason({ isReadOnly: true, cacheReadOnly: true });
  const markup = renderToStaticMarkup(
    React.createElement(
      DisabledActionGate,
      { disabled: true, reason, label: 'Remove all' },
      React.createElement('button', { disabled: true }, 'Remove all')
    )
  );
  const stop = markup.match(/^<div[^>]*><span([^>]*)>/);
  assert.ok(stop, markup);
  assert.match(stop[1], /tabindex="0"/);
  const describedBy = stop[1].match(/aria-describedby="([^"]+)"/);
  assert.ok(describedBy, markup);
  assert.ok(
    markup.includes(`<span id="${describedBy[1]}" class="sr-only">${reason}</span>`),
    markup
  );
});

// The menu as it ships: each item's own gate, the gates around it, and the declarations they read,
// evaluated together for one state.
const menuBase = {
  mockMode: false,
  isCorruptionRemovalActive: false,
  startingRemoveAll: false,
  removeAllPostPending: false,
  anyServiceRemovalPending: false,
  startingRemoveSelected: false,
  checkingPermissions: false,
  directoryMissing: false,
  isReadOnly: false,
  cacheExist: true,
  cacheReadOnly: false,
  isRefreshing: false,
  isScanBusy: false,
  corruptionRemovalBusy: false,
  isAnyRemovalRunning: false,
  scanGate: { available: true, tooltip: '' },
  authMode: 'authenticated',
  hasRemovalPermissionIssue: false,
  nginxReopenAvailable: true,
  nginxReopenUnavailableMessage: '',
  diskObjectsAvailable: true,
  diskObjectDenialReason: 'No cache-key scheme for datasource default',
  scanId: 'scan-1',
  projection: { total: 4 },
  selectedServices: ['steam'],
  allVisibleSelected: false,
  removeSelectedLabel: 'Remove Selected',
  removeAllLabel: 'Remove All',
  detectionMethod: 'structural',
  service: 'steam',
  isRemoving: false,
  loadingDetailsServices: new Set(),
  getServiceDisplayName: (service) => service,
  t: (key, options) => (options?.operation ? `${key}:${options.operation}` : key)
};

const evaluate = (expression, bindings) => bindLifted(`() => (${expression})`, bindings)();

const menuState = (overrides) => {
  const state = { ...menuBase, ...overrides };
  for (const name of [
    'diskObjectReason',
    'loadReason',
    'scanBlocked',
    'scanReason',
    'hasRemovalPermissionIssue',
    'removalBlocked',
    'selectionBlocked',
    'removeAllReason',
    'removeSelectedReason',
    'removeServiceLabel',
    'selectLabel',
    'selectAllLabel'
  ]) {
    const [declaration] = collectNodes(
      sourceFile,
      (node) => typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
    );
    if (declaration) state[name] = evaluate(declaration.initializer.getText(sourceFile), state);
  }
  return state;
};

const menuItems = collectNodes(
  sourceFile,
  (node) =>
    typescript.isJsxElement(node) &&
    ['ActionMenuItem', 'ActionMenuDangerItem'].includes(
      node.openingElement.tagName.getText(sourceFile)
    )
);

const attributeText = (opening, name) =>
  opening.attributes.properties
    .find(
      (property) =>
        typescript.isJsxAttribute(property) && property.name.getText(sourceFile) === name
    )
    ?.initializer?.expression?.getText(sourceFile);

const enclosingGates = (item, tagName) => {
  const gates = [];
  for (let node = item.parent; node; node = node.parent) {
    if (
      typescript.isJsxElement(node) &&
      node.openingElement.tagName.getText(sourceFile) === tagName
    )
      gates.push(node.openingElement);
  }
  return gates;
};

const { DisabledActionGate } = await import(
  await compileToUrl(
    '../src/components/features/management/DisabledActionGate.tsx',
    {
      react: import.meta.resolve('react'),
      '@components/ui/Tooltip': moduleUrl(
        `import React from '${import.meta.resolve('react')}';\n` +
          'export const Tooltip = ({ content, className, children }) =>\n' +
          "  React.createElement('div', { 'data-tooltip': content, className }, children);"
      )
    },
    { jsx: typescript.JsxEmit.React }
  )
);

const disabledText = (opening) => attributeText(opening, 'disabled').replace(/\s+/g, ' ');

// Checks `control` in `state`: it is disabled, its own gate carries a reason and is a keyboard
// stop, and every closed gate around it says the same thing, so the box the pointer opens and the
// reason focus announces agree.
const assertGated = (where, control, state) => {
  const [gate] = enclosingGates(control, 'DisabledActionGate');
  assert.ok(gate, `${where}: no reason gate`);
  assert.equal(
    disabledText(gate),
    disabledText(typescript.isJsxElement(control) ? control.openingElement : control),
    where
  );
  const disabled = evaluate(attributeText(gate, 'disabled'), state);
  const reason = evaluate(attributeText(gate, 'reason'), state);
  const label = evaluate(attributeText(gate, 'label'), state);
  assert.equal(disabled, true, `${where}: not disabled`);
  assert.notEqual(reason, '', `${where}: no reason`);
  const markup = renderToStaticMarkup(
    React.createElement(
      DisabledActionGate,
      { disabled, reason, label },
      React.createElement('button', { disabled }, label)
    )
  );
  assert.match(markup, /tabindex="0"/, `${where}: no keyboard stop`);
  assert.ok(markup.includes(`class="sr-only">${reason}</span>`), `${where}: ${markup}`);
  for (const tagName of ['DiskObjectActionGate', 'NginxReopenActionGate']) {
    for (const outer of enclosingGates(control, tagName)) {
      if (evaluate(attributeText(outer, 'available'), state)) continue;
      assert.equal(
        reason,
        evaluate(attributeText(outer, 'tooltip'), state),
        `${where}: ${tagName}`
      );
    }
  }
};

// Checks every menu item whose text holds `labelText` in `state` with `assertGated`.
const assertExplained = (caseName, state, labelText) => {
  const matches = menuItems.filter((item) => item.getText(sourceFile).includes(labelText));
  assert.ok(matches.length > 0, labelText);
  for (const item of matches) assertGated(`${caseName}, ${labelText}`, item, state);
};

test('every ordinary state that disables a Corruption menu item gives it a reason and a Tab stop', () => {
  const removeSelected = 'removeSelectedLabel';
  const removeAll = 'removeAllLabel';
  const load = "'common.load'";
  const cases = [
    ['nothing selected', { selectedServices: [] }, [removeSelected]],
    [
      'no scan yet',
      { scanId: null, projection: { total: 0 }, selectedServices: [] },
      [removeSelected, removeAll]
    ],
    [
      'the last scan flagged nothing',
      { projection: { total: 0 }, selectedServices: [] },
      [removeSelected, removeAll]
    ],
    ['a scan is running', { isScanBusy: true }, [load]],
    [
      'a corruption removal is running',
      { isCorruptionRemovalActive: true, corruptionRemovalBusy: true },
      [load]
    ],
    ['a cache removal is running', { isAnyRemovalRunning: true }, [load]],
    ['the saved scan is loading', { isRefreshing: true }, [load]]
  ];
  for (const [caseName, overrides, labels] of cases) {
    const state = menuState(overrides);
    for (const labelText of labels) assertExplained(caseName, state, labelText);
  }
});

test('missing disk objects or a closed reopen gate reach the keyboard on every item they disable', () => {
  const items = [
    'management.corruption.fullScan',
    'management.corruption.incrementalScan',
    "t('common.scan')",
    'removeSelectedLabel',
    'removeAllLabel'
  ];
  for (const labelText of items) {
    assertExplained(
      'disk objects unavailable',
      menuState({ diskObjectsAvailable: false }),
      labelText
    );
  }
  for (const labelText of ['removeSelectedLabel', 'removeAllLabel']) {
    assertExplained(
      'the reopen gate is closed',
      menuState({
        nginxReopenAvailable: false,
        nginxReopenUnavailableMessage: 'management.nginxReopen.enablePidHost'
      }),
      labelText
    );
  }
});

test('Load runs when nothing blocks it, and Demo Mode still comes first on Remove selected', () => {
  const [item] = menuItems.filter((node) => node.getText(sourceFile).includes("'common.load'"));
  const state = menuState({});
  const [gate] = enclosingGates(item, 'DisabledActionGate');
  assert.ok(gate, 'Load has no reason gate');
  assert.equal(evaluate(attributeText(gate, 'disabled'), state), false);
  assert.equal(evaluate(attributeText(gate, 'reason'), state), '');
  const calls = [];
  evaluate(attributeText(item.openingElement, 'onClick'), {
    loadCachedData: async (force) => calls.push(['load', force]),
    close: () => calls.push(['close'])
  })();
  assert.deepEqual(calls, [['load', true], ['close']]);
  assert.equal(
    menuState({ mockMode: true, selectedServices: [] }).removeSelectedReason,
    'management.sections.settings.mockModeActive'
  );
});

// The Corruption list: each service row's Remove button and its selection checkbox.
const rowRemove = findSoleNode(
  sourceFile,
  'row Remove button',
  (node) =>
    typescript.isJsxElement(node) &&
    node.openingElement.tagName.getText(sourceFile) === 'Button' &&
    (attributeText(node.openingElement, 'onClick') ?? '').includes('requestServiceRemoval(service)')
);
const rowCheckbox = findSoleNode(
  sourceFile,
  'row checkbox',
  (node) =>
    typescript.isJsxSelfClosingElement(node) &&
    node.tagName.getText(sourceFile) === 'Checkbox' &&
    (attributeText(node, 'onChange') ?? '').includes('selection.toggle(service)')
);

const reopenClosed = {
  nginxReopenAvailable: false,
  nginxReopenUnavailableMessage: 'management.nginxReopen.enablePidHost'
};

// States that disable both the row Remove button and the row checkbox.
const blockingBoth = [
  ['another removal runs', { isCorruptionRemovalActive: true }],
  ['Demo Mode', { mockMode: true }],
  ['the saved scan is refreshing', { isRefreshing: true }],
  ['permissions are loading', { checkingPermissions: true }],
  ['the cache folder is missing', { directoryMissing: true, cacheExist: false }],
  ['the cache folder is read-only', { isReadOnly: true, cacheReadOnly: true }]
];

test('every ordinary state that disables a Corruption row control gives it a reason and a Tab stop', () => {
  for (const [caseName, overrides] of blockingBoth) {
    const state = menuState(overrides);
    assertGated(`${caseName}, row Remove`, rowRemove, state);
    assertGated(`${caseName}, row checkbox`, rowCheckbox, state);
  }
  for (const [caseName, overrides] of [
    ['disk objects unavailable', { diskObjectsAvailable: false }],
    ['the reopen gate is closed', reopenClosed],
    ['its stored evidence is loading', { loadingDetailsServices: new Set(['steam']) }]
  ]) {
    assertGated(`${caseName}, row Remove`, rowRemove, menuState(overrides));
  }
});

test('an enabled Corruption row control shows no reason box, and Remove still runs', () => {
  const [removeGate] = enclosingGates(rowRemove, 'DisabledActionGate');
  const [checkboxGate] = enclosingGates(rowCheckbox, 'DisabledActionGate');
  assert.ok(removeGate, 'row Remove has no reason gate');
  assert.ok(checkboxGate, 'row checkbox has no reason gate');
  const idle = menuState({});
  assert.equal(evaluate(attributeText(removeGate, 'disabled'), idle), false);
  assert.equal(evaluate(attributeText(removeGate, 'reason'), idle), '');
  // Missing disk objects and a closed reopen gate block removal, not selection.
  for (const overrides of [{}, { diskObjectsAvailable: false }, reopenClosed]) {
    const state = menuState(overrides);
    assert.equal(evaluate(attributeText(checkboxGate, 'disabled'), state), false);
    assert.equal(evaluate(attributeText(checkboxGate, 'reason'), state), '');
  }
  const removals = [];
  evaluate(attributeText(rowRemove.openingElement, 'onClick'), {
    requestServiceRemoval: (service) => removals.push(service),
    service: 'steam'
  })();
  assert.deepEqual(removals, ['steam']);
});

test('the row whose removal is running shows its spinner and no reason box', () => {
  const [removeGate] = enclosingGates(rowRemove, 'DisabledActionGate');
  const state = menuState({ isRemoving: true, anyServiceRemovalPending: true });
  assert.equal(evaluate(attributeText(removeGate, 'disabled'), state), true);
  assert.equal(evaluate(attributeText(removeGate, 'reason'), state), '');
  // Another row's Remove still says why it waits.
  const other = menuState({ anyServiceRemovalPending: true });
  assert.notEqual(evaluate(attributeText(removeGate, 'reason'), other), '');
});

const notificationModules = await loadNotificationModules(
  moduleUrl('export default {t:(key)=>key, exists:()=>true, language:"en"};')
);

// The cards the run store draws for corruption removal run rows as the server lists them: the
// service rides only in `target`, never in `serviceId`.
const corruptionRemovalCards = (removals) => {
  let state = notificationModules.createRunStoreState();
  for (const [service, status] of removals) {
    state = pushRun(
      notificationModules,
      state,
      operationRunRow(`removal-${service}`, {
        operationType: 'corruptionRemoval',
        name: `Corruption Removal (${service})`,
        target: service,
        serviceId: null,
        status,
        message: 'common.notifications.operationWaiting'
      })
    );
  }
  return notificationModules.deriveRuns(state);
};

// Whether the shipped row code draws `service`'s row as removing for `runs`.
const rowIsRemoving = (runs, service) => {
  const [activeCheck] = collectNodes(
    sourceFile,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'isActiveCorruptionRemoval'
  );
  const state = {
    runs,
    service,
    isServiceRemovalPending: () => false,
    isActiveCorruptionRemoval: evaluate(activeCheck.initializer.getText(sourceFile), {})
  };
  // The row reads the run list either through one picked removal or directly; evaluate whichever
  // of those declarations the component has, in order.
  for (const name of ['activeRemoval', 'removingService', 'isRemoving']) {
    const [declaration] = collectNodes(
      sourceFile,
      (node) =>
        typescript.isVariableDeclaration(node) &&
        node.name.getText(sourceFile) === name &&
        (name !== 'isRemoving' ||
          node.initializer.getText(sourceFile).includes('isServiceRemovalPending(service)'))
    );
    if (declaration) state[name] = evaluate(declaration.initializer.getText(sourceFile), state);
  }
  return state.isRemoving;
};

test('the row whose own removal is queued or canceling shows its spinner and no reason box', () => {
  const [removeGate] = enclosingGates(rowRemove, 'DisabledActionGate');
  for (const status of ['waiting', 'cancelling']) {
    const isRemoving = rowIsRemoving(corruptionRemovalCards([['steam', status]]), 'steam');
    assert.equal(isRemoving, true, status);
    const state = menuState({ isRemoving, isCorruptionRemovalActive: true });
    assert.equal(evaluate(attributeText(removeGate, 'reason'), state), '', status);
    assert.equal(evaluate(attributeText(rowRemove.openingElement, 'loading'), state), true, status);
  }
});

test('every row with an active removal keeps its spinner, whichever removal is listed first', () => {
  const runs = corruptionRemovalCards([
    ['steam', 'waiting'],
    ['epicgames', 'running']
  ]);
  assert.equal(rowIsRemoving(runs, 'steam'), true);
  assert.equal(rowIsRemoving(runs, 'epicgames'), true);
  assert.equal(rowIsRemoving(runs, 'xbox'), false);
});

test('while a removal runs or waits in the queue, the other rows say so', () => {
  assert.equal(
    removeAllReason({ isCorruptionRemovalActive: true }),
    'management.corruption.removalActive'
  );
});

test('while a corruption removal or scan waits in the queue, Load says it runs or waits', () => {
  const active = 'management.corruption.removalActive';
  assert.equal(
    menuState({ corruptionRemovalBusy: true }).loadReason,
    `${active}:common.notifications.titles.corruptionRemoval`
  );
  assert.equal(
    menuState({ isScanBusy: true }).loadReason,
    `${active}:common.notifications.titles.corruptionDetection`
  );
});

const selectAll = findSoleNode(
  sourceFile,
  'Select all checkbox',
  (node) =>
    typescript.isJsxSelfClosingElement(node) &&
    node.tagName.getText(sourceFile) === 'Checkbox' &&
    (attributeText(node, 'onChange') ?? '').includes('selection.setMany(serviceKeys')
);

test('Select all gives the row checkboxes their reason and a Tab stop in every state that disables them', () => {
  const selectAllBase = { serviceKeys: ['steam'] };
  const [gate] = enclosingGates(selectAll, 'DisabledActionGate');
  assert.ok(gate, 'Select all has no reason gate');
  const [rowGate] = enclosingGates(rowCheckbox, 'DisabledActionGate');
  for (const [caseName, overrides] of [
    ...blockingBoth,
    ['a one-service removal is pending', { anyServiceRemovalPending: true }]
  ]) {
    const state = menuState({ ...overrides, ...selectAllBase });
    assert.equal(evaluate(attributeText(selectAll, 'disabled'), state), true, caseName);
    const disabled = evaluate(attributeText(gate, 'disabled'), state);
    const reason = evaluate(attributeText(gate, 'reason'), state);
    assert.equal(disabled, true, `${caseName}: gate not disabled`);
    assert.equal(reason, evaluate(attributeText(rowGate, 'reason'), state), caseName);
    assert.notEqual(reason, '', `${caseName}: no reason`);
    const label = evaluate(attributeText(gate, 'label'), state);
    assert.equal(label, evaluate(attributeText(selectAll, 'label'), state), caseName);
    const markup = renderToStaticMarkup(
      React.createElement(
        DisabledActionGate,
        { disabled, reason, label },
        React.createElement('input', { type: 'checkbox', disabled })
      )
    );
    assert.match(markup, /tabindex="0"/, `${caseName}: no keyboard stop`);
    assert.ok(markup.includes(`class="sr-only">${reason}</span>`), `${caseName}: ${markup}`);
  }
  const idle = menuState(selectAllBase);
  assert.equal(evaluate(attributeText(gate, 'disabled'), idle), false);
  assert.equal(evaluate(attributeText(selectAll, 'disabled'), idle), false);
});

test('Select all stays usable and shows no reason box when only removal is blocked', () => {
  const [gate] = enclosingGates(selectAll, 'DisabledActionGate');
  for (const [caseName, overrides] of [
    ['disk objects unavailable', { diskObjectsAvailable: false }],
    ['the reopen gate is closed', reopenClosed]
  ]) {
    const state = menuState({ ...overrides, serviceKeys: ['steam'] });
    assert.notEqual(state.removeAllReason, '', `${caseName}: removal is not blocked`);
    assert.equal(evaluate(attributeText(selectAll, 'disabled'), state), false, caseName);
    assert.equal(evaluate(attributeText(gate, 'disabled'), state), false, caseName);
    assert.equal(evaluate(attributeText(gate, 'reason'), state), '', caseName);
  }
});

/**
 * A removal dialog closes only once the server accepts the removal. A refusal stays inside the open
 * dialog with its reason, and Confirm shows busy only while the request runs. The handlers are
 * lifted out of the components as shipped and driven against a server that refuses or accepts.
 */

const storagePath = 'src/components/features/management/sections/StorageSection.tsx';
const detectorPath = 'src/components/features/management/game-detection/GameCacheDetector.tsx';

// Records every state write, and the dialog's open state, through a handler run.
const dialogState = (opened) => {
  const writes = [];
  const state = { opened, error: null, busy: [] };
  return {
    state,
    writes,
    setOpened: (next) => {
      writes.push('opened');
      state.opened = typeof next === 'function' ? next(state.opened) : next;
    },
    setError: (next) => {
      state.error = next;
    },
    setBusy: (next) => {
      state.busy.push(next);
    }
  };
};

const getErrorMessage = (err) => err.message;

test('a refused Remove all evicted keeps its dialog open with the reason; an accepted one closes it', async () => {
  const handlerSource = liftHookCallback(storagePath, 'useCallback', 'removeAllEvicted()');
  for (const refused of [true, false]) {
    const dialog = dialogState(true);
    const handler = bindLifted(handlerSource, {
      removeAllRunning: false,
      isAdmin: true,
      allEvictedNginxReopenGate: { available: true },
      evictedServices: [{ service_name: 'steam' }],
      evictedGames: [],
      setShowRemoveAllConfirm: dialog.setOpened,
      setRemoveAllRunning: dialog.setBusy,
      setRemoveAllEvictedError: dialog.setError,
      getErrorMessage,
      ApiService: {
        removeAllEvicted: () =>
          refused ? Promise.reject(new Error('A download is in progress')) : Promise.resolve()
      }
    });

    await handler();

    assert.equal(dialog.state.opened, refused, `refused=${refused}`);
    assert.equal(dialog.state.error, refused ? 'A download is in progress' : null);
    assert.deepEqual(dialog.state.busy, [true, false]);
  }
});

test('a refused evicted-records removal keeps its dialog open with the reason; an accepted one closes it', async () => {
  const handlerSource = liftConstArrow(storagePath, 'confirmEvictedRemoval');
  for (const refused of [true, false]) {
    const service = { service_name: 'steam' };
    const dialog = dialogState(service);
    const target = { current: null };
    let refreshed = false;
    const handler = bindLifted(handlerSource, {
      evictedTarget: service,
      evictedConfirming: false,
      setEvictedConfirming: dialog.setBusy,
      setEvictedTarget: dialog.setOpened,
      setEvictedError: dialog.setError,
      removalTargetRef: target,
      classifyGameFromCacheInfo: () => {
        throw new Error('only a service is removed here');
      },
      getErrorMessage,
      t: (key) => key,
      FAILED_TO_REMOVE_GAME_I18N_KEY: 'failed',
      scheduleRemovalRefresh: () => {
        refreshed = true;
      },
      onDataRefresh: () => undefined,
      ApiService: {
        removeEvictedForService: () =>
          refused ? Promise.reject(new Error('A download is in progress')) : Promise.resolve()
      }
    });

    await handler();

    assert.equal(dialog.state.opened, refused ? service : null, `refused=${refused}`);
    assert.equal(dialog.state.error, refused ? 'A download is in progress' : null);
    assert.deepEqual(dialog.state.busy, [true, false]);
    assert.equal(refreshed, !refused);
    // A removal that never started leaves no target for a later completion to prune.
    assert.deepEqual(target.current, refused ? null : { serviceName: 'steam' });
  }
});

test('a refused game or service removal keeps its dialog open with the reason; an accepted one closes it', async () => {
  for (const [name, runner, targetName, setterName, item] of [
    [
      'confirmRemoval',
      'runTrackedGameRemoval',
      'gameToRemove',
      'setGameToRemove',
      { game_name: 'G' }
    ],
    [
      'confirmServiceRemoval',
      'runTrackedServiceRemoval',
      'serviceToRemove',
      'setServiceToRemove',
      { service_name: 'steam' }
    ]
  ]) {
    const handlerSource = liftConstArrow(detectorPath, name);
    for (const refused of [true, false]) {
      const dialog = dialogState(item);
      const handler = bindLifted(handlerSource, {
        [targetName]: item,
        removalConfirming: false,
        setRemovalConfirming: dialog.setBusy,
        [setterName]: dialog.setOpened,
        setRemovalError: dialog.setError,
        getErrorMessage,
        t: (key) => key,
        scheduleRemovalRefresh: () => undefined,
        onDataRefresh: () => undefined,
        // The shared helper reports a request the server refused through notifyError.
        [runner]: async ({ notifyError }) => {
          if (refused) notifyError('failed', new Error('A download is in progress'));
        }
      });

      await handler();

      assert.equal(dialog.state.opened, refused ? item : null, `${name} refused=${refused}`);
      assert.equal(dialog.state.error, refused ? 'A download is in progress' : null);
      assert.deepEqual(dialog.state.busy, [true, false]);
    }
  }
});
