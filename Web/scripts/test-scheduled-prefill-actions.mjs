import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';

import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  parseSource
} from './transpile-module.mjs';

const detailSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillScheduleDetail.tsx',
  ts.ScriptKind.TSX
);
const panelSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPlatformsPanel.tsx',
  ts.ScriptKind.TSX
);
const persistentCardSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPersistentCard.tsx',
  ts.ScriptKind.TSX
);
const actionMenuSource = parseSource('src/components/ui/ActionMenu.tsx', ts.ScriptKind.TSX);
const focusUrl = await compileToUrl('../src/utils/focus.ts');
const { getFocusable } = await import(focusUrl);
globalThis.HTMLInputElement = class HTMLInputElement {};

function getTagName(element) {
  return element.openingElement.tagName.getText();
}

function getAttribute(element, source, name) {
  const attribute = element.openingElement.attributes.properties.find(
    (property) => ts.isJsxAttribute(property) && property.name.text === name
  );
  assert.ok(attribute, `${getTagName(element)} has ${name}`);
  assert.ok(attribute.initializer, `${getTagName(element)} ${name} has a value`);
  if (ts.isStringLiteral(attribute.initializer)) return JSON.stringify(attribute.initializer.text);
  assert.ok(ts.isJsxExpression(attribute.initializer));
  assert.ok(attribute.initializer.expression, `${getTagName(element)} ${name} has an expression`);
  return attribute.initializer.expression.getText(source);
}

function hasAttribute(element, name) {
  return element.openingElement.attributes.properties.some(
    (property) => ts.isJsxAttribute(property) && property.name.text === name
  );
}

function getHandler(element, source) {
  const handler = getAttribute(element, source, 'onClick');
  assert.match(handler, /^\(?.*?\)?\s*=>/s, 'menu items use executable callbacks');
  return handler;
}

function getComponent(source, name) {
  return findSoleNode(
    source,
    `${name} component`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
}

function getMenuItems(component, source, tag) {
  return collectNodes(component, (node) => ts.isJsxElement(node) && getTagName(node) === tag);
}

function getItemWithCallback(component, source, tag, callbackName) {
  return getMenuItems(component, source, tag).find((item) =>
    getHandler(item, source).includes(`${callbackName}(`)
  );
}

test('row Actions callbacks keep a pending operation scoped to its exact service and schedule', () => {
  const row = getComponent(detailSource, 'ScheduledPrefillServiceScheduleRow');
  const opened = [];
  const runs = [];
  const cancels = [];
  const opens = [];
  const toggles = [];
  const bindings = {
    setActionsOpen: (value) => opened.push(value),
    onRun: (...args) => runs.push(args),
    onCancel: (...args) => cancels.push(args),
    onOpen: (...args) => opens.push(args),
    onToggleEnabled: (...args) => toggles.push(args),
    serviceId: 'Steam',
    scheduleId: 'schedule-a',
    serviceKey: 'steam'
  };

  for (const [tag, callbackName, expected] of [
    ['ActionMenuItem', 'onRun', runs],
    ['ActionMenuDangerItem', 'onCancel', cancels],
    ['ActionMenuItem', 'onOpen', opens],
    ['ActionMenuItem', 'onToggleEnabled', toggles]
  ]) {
    const item = getItemWithCallback(row, detailSource, tag, callbackName);
    assert.ok(item, `${callbackName} action is rendered`);
    bindLifted(getHandler(item, detailSource), bindings)();
    assert.equal(opened.pop(), false, `${callbackName} closes the menu before acting`);
    assert.deepEqual(
      expected.pop(),
      callbackName === 'onOpen' || callbackName === 'onToggleEnabled'
        ? ['steam', 'schedule-a']
        : ['Steam', 'schedule-a']
    );
  }
});

test('row and record Actions offer Enable for an off schedule and Disable for an on one', () => {
  // The row item is one control that reads the schedule's state: an off row says Enable, an on
  // row says Disable, and neither state hides it.
  const row = getComponent(detailSource, 'ScheduledPrefillServiceScheduleRow');
  const rowItem = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onToggleEnabled');
  assert.ok(rowItem);
  const rowLabel = rowItem.children
    .map((child) => child.getText(detailSource))
    .join('')
    .trim();
  assert.equal(rowLabel, "{t(`${baseKey}.records.${enabled ? 'disable' : 'enable'}`)}");
  assert.equal(getAttribute(rowItem, detailSource, 'disabled'), 'actionsDisabled');

  // Run implies the schedule is on: an off row's menu is Open + Enable only, while a run that is
  // already in flight keeps its Cancel regardless.
  const gateOf = (item) => {
    let gate = item.parent;
    while (ts.isParenthesizedExpression(gate)) gate = gate.parent;
    assert.ok(ts.isBinaryExpression(gate), 'the item sits behind a condition');
    return gate.left.getText(detailSource);
  };
  const runItem = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onRun');
  assert.equal(gateOf(runItem), '!isRunning && enabled');
  const cancelItem = getItemWithCallback(row, detailSource, 'ActionMenuDangerItem', 'onCancel');
  assert.equal(gateOf(cancelItem), 'isRunning');

  // The record menu in the Configure modal flips the draft the same way the Off/On toggle does.
  const panel = getComponent(panelSource, 'ScheduledPrefillPlatformsPanel');
  const recordItem = getItemWithCallback(panel, panelSource, 'ActionMenuItem', 'onScheduleChange');
  assert.ok(recordItem);
  const recordLabel = recordItem.children
    .map((child) => child.getText(panelSource))
    .join('')
    .replace(/\s+/g, ' ')
    .trim();
  assert.equal(
    recordLabel,
    "{t( `${baseKey}.records.${activeSchedule?.enabled ? 'disableSchedule' : 'enableSchedule'}` )}"
  );
  const changes = [];
  const closed = [];
  bindLifted(getHandler(recordItem, panelSource), {
    setActionsOpen: (value) => closed.push(value),
    onScheduleChange: (...args) => changes.push(args),
    activeServiceKey: 'steam',
    activeSchedule: { id: 'schedule-a', name: 'Default', enabled: true }
  })();
  assert.equal(closed.pop(), false);
  assert.deepEqual(changes.pop(), ['steam', { id: 'schedule-a', name: 'Default', enabled: false }]);
});

test('row Actions shares one disabled rule between its trigger and every item', () => {
  const row = getComponent(detailSource, 'ScheduledPrefillServiceScheduleRow');
  const menuTrigger = getMenuItems(row, detailSource, 'Button').find(
    (button) => getAttribute(button, detailSource, 'variant') === '"menu"'
  );
  assert.ok(menuTrigger, 'row has a menu trigger');
  assert.equal(getAttribute(menuTrigger, detailSource, 'disabled'), 'actionsDisabled');
  // The Enable round-trip holds the trigger disabled through `actionsDisabled` and announces
  // itself with the notification; a spinner on the trigger flashed on every click, so no
  // scheduled-prefill Actions trigger carries `loading`.
  assert.equal(hasAttribute(menuTrigger, 'loading'), false, 'row trigger has no spinner');
  const panelTrigger = getMenuItems(
    getComponent(panelSource, 'ScheduledPrefillPlatformsPanel'),
    panelSource,
    'Button'
  ).find((button) => getAttribute(button, panelSource, 'variant') === '"menu"');
  assert.ok(panelTrigger, 'record group has a menu trigger');
  assert.equal(hasAttribute(panelTrigger, 'loading'), false, 'record trigger has no spinner');
  const footerTrigger = getMenuItems(
    getComponent(persistentCardSource, 'ScheduledPrefillPersistentCard'),
    persistentCardSource,
    'Button'
  ).find((button) => getAttribute(button, persistentCardSource, 'variant') === '"menu"');
  assert.ok(footerTrigger, 'persistent card footer has a menu trigger');
  assert.equal(hasAttribute(footerTrigger, 'loading'), false, 'footer trigger has no spinner');

  const run = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onRun');
  const cancel = getItemWithCallback(row, detailSource, 'ActionMenuDangerItem', 'onCancel');
  const open = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onOpen');
  const enable = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onToggleEnabled');
  assert.ok(run);
  assert.ok(cancel);
  assert.ok(open);
  assert.ok(enable);
  // A menu already open when the row turns disabled keeps its portalled items mounted, so the
  // trigger's disabled state alone does not stop them firing. Every item repeats the rule, and
  // the two that own an extra pending flag add it to that shared one rather than replacing it.
  assert.equal(
    getAttribute(run, detailSource, 'disabled'),
    'actionsDisabled || runDisabled || runPending'
  );
  assert.equal(getAttribute(cancel, detailSource, 'disabled'), 'actionsDisabled || cancelPending');
  assert.equal(getAttribute(open, detailSource, 'disabled'), 'actionsDisabled');
  assert.equal(getAttribute(enable, detailSource, 'disabled'), 'actionsDisabled');

  // The shared rule reads the row's own disabled state plus its Enable round-trip, and stays
  // blind to `enabled`: an off schedule keeps a working menu, because Enable is inside it.
  const actionsDisabled = findSoleNode(
    detailSource,
    'row actionsDisabled rule',
    (node) =>
      ts.isVariableDeclaration(node) && node.name.getText(detailSource) === 'actionsDisabled'
  );
  assert.equal(actionsDisabled.initializer.getText(detailSource), 'disabled || enablePending');

  const stateUpdates = [];
  bindLifted(getHandler(menuTrigger, detailSource), {
    setActionsOpen: (value) => stateUpdates.push(value)
  })();
  assert.equal(stateUpdates.length, 1);
  assert.equal(stateUpdates[0](true), false, 'the mounted trigger can close its own menu');
});

test('record-group Actions closes before new, save-as, and delete callbacks', () => {
  const panel = getComponent(panelSource, 'ScheduledPrefillPlatformsPanel');
  const closed = [];
  const added = [];
  const duplicated = [];
  const deleted = [];
  const shown = [];
  const bindings = {
    setActionsOpen: (value) => closed.push(value),
    // Both creators hand back the id of the record they made, which is what makes it the one
    // on screen. A creator that returned nothing would leave the reader on the old record.
    onAddSchedule: (...args) => {
      added.push(args);
      return 'schedule-new';
    },
    onDuplicateSchedule: (...args) => {
      duplicated.push(args);
      return 'schedule-copy';
    },
    onDeleteSchedule: (...args) => deleted.push(args),
    showNewSchedule: (scheduleId) => shown.push(scheduleId),
    activeServiceKey: 'steam',
    activeSchedule: { id: 'schedule-a' }
  };

  for (const [tag, callbackName, calls] of [
    ['ActionMenuItem', 'onAddSchedule', added],
    ['ActionMenuItem', 'onDuplicateSchedule', duplicated],
    ['ActionMenuDangerItem', 'onDeleteSchedule', deleted]
  ]) {
    const item = getItemWithCallback(panel, panelSource, tag, callbackName);
    assert.ok(item, `${callbackName} action is rendered`);
    bindLifted(getHandler(item, panelSource), bindings)();
    assert.equal(closed.pop(), false, `${callbackName} closes the menu before acting`);
    assert.deepEqual(
      calls.pop(),
      callbackName === 'onAddSchedule' ? ['steam'] : ['steam', 'schedule-a']
    );
  }

  assert.deepEqual(shown, ['schedule-new', 'schedule-copy'], 'a new record becomes the shown one');

  const deleteItem = getItemWithCallback(
    panel,
    panelSource,
    'ActionMenuDangerItem',
    'onDeleteSchedule'
  );
  assert.ok(deleteItem);
  assert.equal(
    getAttribute(deleteItem, panelSource, 'disabled'),
    '!activeSchedule || activeService.schedules.length === 1'
  );
});

test('Actions Escape closes only the menu and restores focus to its trigger', () => {
  const escapeHandler = findSoleNode(
    actionMenuSource,
    'ActionMenu capture Escape handler',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === 'handleEscape' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);
  const closeCalls = [];
  const stopCalls = [];
  const focusCalls = [];
  const handleEscape = bindLifted(escapeHandler, {
    onClose: () => closeCalls.push(true),
    requestAnimationFrame: (callback) => callback(),
    focusTrigger: () => focusCalls.push(true)
  });

  handleEscape({ key: 'Escape', stopPropagation: () => stopCalls.push(true) });
  assert.deepEqual(stopCalls, [true]);
  assert.deepEqual(closeCalls, [true]);
  assert.deepEqual(focusCalls, [true]);

  handleEscape({ key: 'Enter', stopPropagation: () => stopCalls.push(true) });
  assert.deepEqual(closeCalls, [true]);
});

test('Actions Tab enters enabled portalled items and exits without stranding focus', () => {
  const triggerHandler = findSoleNode(
    actionMenuSource,
    'ActionMenu trigger Tab handler',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === 'handleTriggerKeyDown' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);
  const menuHandler = findSoleNode(
    actionMenuSource,
    'ActionMenu item Tab handler',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === 'handleMenuKeyDown' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);
  const firstItem = {
    offsetParent: {},
    focusCalls: 0,
    focus() {
      this.focusCalls += 1;
    }
  };
  const middleItem = { offsetParent: {}, focus: () => undefined };
  const lastItem = { offsetParent: {}, focus: () => undefined };
  const menuRoot = { querySelectorAll: () => [firstItem, middleItem, lastItem] };
  const entered = [];
  const triggerClosed = [];
  const handleTriggerKeyDown = bindLifted(triggerHandler, {
    isOpen: true,
    dropdownRef: { current: menuRoot },
    getFocusable,
    onClose: () => triggerClosed.push(true)
  });

  handleTriggerKeyDown({
    key: 'Tab',
    shiftKey: false,
    preventDefault: () => entered.push('prevent'),
    stopPropagation: () => entered.push('stop')
  });
  assert.deepEqual(entered, ['prevent', 'stop']);
  assert.equal(firstItem.focusCalls, 1, 'Tab enters the first enabled item');

  const reverse = [];
  handleTriggerKeyDown({
    key: 'Tab',
    shiftKey: true,
    preventDefault: () => reverse.push('prevent'),
    stopPropagation: () => reverse.push('stop')
  });
  assert.deepEqual(reverse, [], 'Shift+Tab keeps native reverse traversal');
  assert.deepEqual(triggerClosed, [true], 'Shift+Tab closes the open menu');
  assert.match(
    actionMenuSource.text,
    /import \{ getFocusable \} from '@utils\/focus';/,
    'Actions uses the shared focus query'
  );

  const closed = [];
  const triggerFocus = [];
  const nextFocus = [];
  const handleMenuKeyDown = bindLifted(menuHandler, {
    dropdownRef: { current: menuRoot },
    getFocusable,
    onClose: () => closed.push(true),
    focusTrigger: () => triggerFocus.push(true),
    focusNextAfterTrigger: () => nextFocus.push(true)
  });
  const shifted = [];
  handleMenuKeyDown({
    key: 'Tab',
    shiftKey: true,
    target: firstItem,
    preventDefault: () => shifted.push('prevent'),
    stopPropagation: () => shifted.push('stop')
  });
  assert.deepEqual(shifted, ['prevent', 'stop']);
  assert.deepEqual(closed, [true]);
  assert.deepEqual(triggerFocus, [true]);

  const forward = [];
  handleMenuKeyDown({
    key: 'Tab',
    shiftKey: false,
    target: lastItem,
    preventDefault: () => forward.push('prevent'),
    stopPropagation: () => forward.push('stop')
  });
  assert.deepEqual(forward, ['prevent', 'stop']);
  assert.deepEqual(closed, [true, true]);
  assert.deepEqual(nextFocus, [true]);

  const middle = [];
  handleMenuKeyDown({
    key: 'Tab',
    shiftKey: false,
    target: middleItem,
    preventDefault: () => middle.push('prevent'),
    stopPropagation: () => middle.push('stop')
  });
  assert.deepEqual(middle, [], 'intermediate menu items retain native Tab order');
});
