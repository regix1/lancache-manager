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
  const enables = [];
  const bindings = {
    setActionsOpen: (value) => opened.push(value),
    onRun: (...args) => runs.push(args),
    onCancel: (...args) => cancels.push(args),
    onOpen: (...args) => opens.push(args),
    onEnable: (...args) => enables.push(args),
    serviceId: 'Steam',
    scheduleId: 'schedule-a',
    serviceKey: 'steam'
  };

  for (const [tag, callbackName, expected] of [
    ['ActionMenuItem', 'onRun', runs],
    ['ActionMenuDangerItem', 'onCancel', cancels],
    ['ActionMenuItem', 'onOpen', opens],
    ['ActionMenuItem', 'onEnable', enables]
  ]) {
    const item = getItemWithCallback(row, detailSource, tag, callbackName);
    assert.ok(item, `${callbackName} action is rendered`);
    bindLifted(getHandler(item, detailSource), bindings)();
    assert.equal(opened.pop(), false, `${callbackName} closes the menu before acting`);
    assert.deepEqual(
      expected.pop(),
      callbackName === 'onOpen' || callbackName === 'onEnable'
        ? ['steam', 'schedule-a']
        : ['Steam', 'schedule-a']
    );
  }
});

test('row Actions keeps its trigger interactive while pending and disables only the pending item', () => {
  const row = getComponent(detailSource, 'ScheduledPrefillServiceScheduleRow');
  const menuTrigger = getMenuItems(row, detailSource, 'Button').find(
    (button) => getAttribute(button, detailSource, 'variant') === '"menu"'
  );
  assert.ok(menuTrigger, 'row has a menu trigger');
  assert.equal(getAttribute(menuTrigger, detailSource, 'disabled'), 'disabled');

  const run = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onRun');
  const cancel = getItemWithCallback(row, detailSource, 'ActionMenuDangerItem', 'onCancel');
  assert.ok(run);
  assert.ok(cancel);
  assert.equal(getAttribute(run, detailSource, 'disabled'), 'runDisabled || runPending');
  assert.equal(getAttribute(cancel, detailSource, 'disabled'), 'cancelPending');

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
  const bindings = {
    setActionsOpen: (value) => closed.push(value),
    onAddSchedule: (...args) => added.push(args),
    onDuplicateSchedule: (...args) => duplicated.push(args),
    onDeleteSchedule: (...args) => deleted.push(args),
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
