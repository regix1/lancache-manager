import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

import {
  bindLifted,
  collectNodes,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
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
const platformSectionSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPlatformSection.tsx',
  ts.ScriptKind.TSX
);
const configModalSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
  ts.ScriptKind.TSX
);
const persistentCardSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPersistentCard.tsx',
  ts.ScriptKind.TSX
);
const actionMenuSource = parseSource('src/components/ui/ActionMenu.tsx', ts.ScriptKind.TSX);
const schedulesCss = readFileSync(
  new URL('../src/components/features/management/schedules/SchedulesSection.css', import.meta.url),
  'utf8'
);
const focusUrl = await compileToUrl('../src/utils/focus.ts');
const { getFocusable } = await import(focusUrl);
globalThis.HTMLInputElement = class HTMLInputElement {};

function getTagName(element) {
  return element.openingElement.tagName.getText();
}

function getAttribute(element, source, name) {
  const attribute = (element.openingElement ?? element).attributes.properties.find(
    (property) => ts.isJsxAttribute(property) && property.name.text === name
  );
  assert.ok(attribute, `element has ${name}`);
  assert.ok(attribute.initializer, `${name} has a value`);
  if (ts.isStringLiteral(attribute.initializer)) return JSON.stringify(attribute.initializer.text);
  assert.ok(ts.isJsxExpression(attribute.initializer));
  assert.ok(attribute.initializer.expression, `${name} has an expression`);
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

function modalSession() {
  const pending = { config: [], validity: [] };
  const state = {
    config: null,
    loading: false,
    validity: 90,
    savedValidity: null,
    loadError: null,
    settingsError: null
  };
  const bindings = {
    opened: true,
    persistedConfigRef: { current: null },
    loadedConfigRef: { current: null },
    savedValidityDaysRef: { current: null },
    persistentContainersRequestRef: { current: null },
    editSessionRef: { current: null },
    editSessionRetiredRef: { current: false },
    sessionStore: {},
    loadScheduledPrefillEditSession: () => null,
    retryStoredEditSessionCleanup: async () => undefined,
    setEditSessionCleanupPending: () => undefined,
    setConfig: (config) => {
      state.config = config;
    },
    setLoadingConfig: (loading) => {
      state.loading = loading;
    },
    setLoadError: (error) => {
      state.loadError = error;
    },
    setPersistentValidityDays: (days) => {
      state.validity = days;
    },
    setSavedValidityDays: (days) => {
      state.savedValidity = days;
      bindings.savedValidityDaysRef.current = days;
    },
    setGlobalSettingsError: (error) => {
      state.settingsError = error;
    },
    setValidationError: () => undefined,
    setSaveError: () => undefined,
    setPersistentError: () => undefined,
    setGameSelectionError: () => undefined,
    setGameSelection: () => undefined,
    setPersistentLoginTarget: () => undefined,
    setSaving: () => undefined,
    loadPersistentContainers: () => undefined,
    DEFAULT_PERSISTENT_PREFILL_VALIDITY_DAYS: 90,
    PERSISTENT_PREFILL_VALIDITY_BOUNDS: { min: 1, max: 365 },
    clampToBounds: (days) => days,
    reconcileScheduledPrefillConfig: (config) => config,
    isAbortError: (error) => error.name === 'AbortError',
    getErrorMessage: (error) => error.message,
    ApiService: {
      getScheduledPrefillConfig: () =>
        new Promise((resolve, reject) => pending.config.push({ resolve, reject })),
      getPersistentPrefillValidity: () =>
        new Promise((resolve, reject) => pending.validity.push({ resolve, reject }))
    }
  };
  for (const [name, token] of [
    ['loadConfig', 'getScheduledPrefillConfig'],
    ['loadGlobalSettings', 'getPersistentPrefillValidity']
  ]) {
    bindings[name] = bindLifted(
      liftHookCallback(configModalSource.fileName, 'useCallback', token),
      bindings
    );
  }
  return {
    state,
    bindings,
    pending,
    open: () =>
      bindLifted(
        liftHookCallback(configModalSource.fileName, 'useEffect', 'const storedEditSession'),
        bindings
      )()
  };
}

test('first load remains pending while reopen restores confirmed config and validity instead of discarded edits', () => {
  const f = modalSession();
  const close = f.open();
  assert.equal(f.state.config, null);
  assert.equal(f.state.loading, true);
  close();
  const confirmed = { version: 6, steam: { enabled: true } };
  f.bindings.persistedConfigRef.current = confirmed;
  f.bindings.savedValidityDaysRef.current = 120;
  f.state.config = { version: 6, steam: { enabled: false } };
  f.state.validity = 30;
  f.open();
  assert.equal(f.state.config, confirmed);
  assert.equal(f.state.validity, 120);
  assert.equal(f.state.loading, true, 'edits remain gated during authoritative refresh');
  const opening = liftHookCallback(
    configModalSource.fileName,
    'useEffect',
    'const storedEditSession'
  );
  assert.doesNotMatch(opening, /setPersistentContainers\(/, 'confirmed containers remain mounted');
});

for (const outcome of ['success', 'failure']) {
  test(`an aborted opening ${outcome} cannot write config, settings or loading over the newer opening`, async () => {
    const f = modalSession();
    const old = new AbortController();
    const oldConfig = f.bindings.loadConfig(old.signal);
    const oldSettings = f.bindings.loadGlobalSettings(old.signal);
    old.abort();
    const currentConfig = f.bindings.loadConfig(new AbortController().signal);
    const currentSettings = f.bindings.loadGlobalSettings(new AbortController().signal);
    if (outcome === 'success') {
      f.pending.config[0].resolve({ old: true });
      f.pending.validity[0].resolve({ days: 10 });
    } else {
      f.pending.config[0].reject(new Error('old config failed'));
      f.pending.validity[0].reject(new Error('old settings failed'));
    }
    await Promise.all([oldConfig, oldSettings]);
    assert.equal(f.state.config, null);
    assert.equal(f.state.loading, true);
    assert.equal(f.state.loadError, null);
    assert.equal(f.state.settingsError, null);
    assert.equal(f.state.savedValidity, null);
    const confirmed = { current: true };
    f.pending.config[1].resolve(confirmed);
    f.pending.validity[1].resolve({ days: 120 });
    await Promise.all([currentConfig, currentSettings]);
    assert.equal(f.state.config, confirmed);
    assert.equal(f.bindings.persistedConfigRef.current, confirmed);
    assert.equal(f.bindings.loadedConfigRef.current, JSON.stringify(confirmed));
    assert.equal(f.state.validity, 120);
    assert.equal(f.state.savedValidity, 120);
    assert.equal(f.state.loading, false);
  });
}

test('current refresh failures retain confirmed content and prevent writing an unread validity', async () => {
  const f = modalSession();
  const confirmed = { current: true };
  f.state.config = confirmed;
  f.state.savedValidity = 120;
  const config = f.bindings.loadConfig();
  const settings = f.bindings.loadGlobalSettings();
  f.pending.config[0].reject(new Error('config failed'));
  f.pending.validity[0].reject(new Error('validity failed'));
  await Promise.all([config, settings]);
  assert.equal(f.state.config, confirmed);
  assert.equal(f.state.loading, false);
  assert.equal(f.state.loadError, 'config failed');
  assert.equal(f.state.settingsError, 'validity failed');
  assert.equal(f.state.savedValidity, null);
});

test('a successful save confirms the submitted snapshot before callbacks and the next open', async () => {
  const f = modalSession();
  const submitted = { version: 6, steam: { enabled: true } };
  const written = [];
  const commit = findSoleNode(
    configModalSource,
    'commitSave',
    (node) =>
      ts.isVariableDeclaration(node) && node.name.getText(configModalSource) === 'commitSave'
  ).initializer.getText(configModalSource);
  f.bindings.ApiService.updateScheduledPrefillConfig = async (config) => written.push(config);
  f.bindings.ApiService.updatePersistentPrefillValidity = async (value) => written.push(value);
  await bindLifted(commit, {
    ...f.bindings,
    config: submitted,
    savedValidityDays: 90,
    persistentValidityDays: 120,
    onSaved: () => {
      assert.equal(f.bindings.persistedConfigRef.current, submitted);
      assert.equal(f.bindings.loadedConfigRef.current, JSON.stringify(submitted));
    },
    onClose: () => undefined
  })();
  assert.deepEqual(written, [submitted, { days: 120 }]);
  f.state.config = { discarded: true };
  f.open();
  assert.equal(f.state.config, submitted);
  assert.equal(f.state.validity, 120);
});

test('Steam refresh invalidates only private availability and close cancels reconnect requests', () => {
  const effects = collectNodes(
    configModalSource,
    (node) =>
      ts.isCallExpression(node) && node.expression.getText(configModalSource) === 'useEffect'
  );
  const availability = effects.find((node) =>
    node.arguments[0].getText(configModalSource).includes('void loadIntegrationLoginAvailability(')
  );
  const opening = effects.find((node) =>
    node.arguments[0].getText(configModalSource).includes('const storedEditSession')
  );
  assert.match(availability.arguments[1].getText(configModalSource), /\brevision\b/);
  assert.doesNotMatch(opening.arguments[1].getText(configModalSource), /\brevision\b/);
  const current = new AbortController();
  const ref = { current };
  let caller;
  const cleanup = bindLifted(availability.arguments[0].getText(configModalSource), {
    opened: true,
    integrationLoginRequestRef: ref,
    loadIntegrationLoginAvailability: (signal) => {
      caller = signal;
    }
  })();
  ref.current = new AbortController();
  cleanup();
  assert.equal(caller.aborted, true);
  assert.equal(ref.current.signal.aborted, true, 'close cancels a request started by reconnect');
});

test('row Actions callbacks keep a pending operation scoped to its exact service and schedule', () => {
  const row = getComponent(detailSource, 'ScheduledPrefillServiceScheduleRow');
  const opened = [];
  const runs = [];
  const opens = [];
  const toggles = [];
  const bindings = {
    setActionsOpen: (value) => opened.push(value),
    onRun: (...args) => runs.push(args),
    onOpen: (...args) => opens.push(args),
    onToggleEnabled: (...args) => toggles.push(args),
    serviceId: 'Steam',
    scheduleId: 'schedule-a',
    serviceKey: 'steam'
  };

  for (const [tag, callbackName, expected] of [
    ['ActionMenuItem', 'onRun', runs],
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

  // Run implies the schedule is on: an off row's menu is Open + Enable only. Cancellation belongs
  // to the global notification control instead of the schedule row.
  const gateOf = (item) => {
    let gate = item.parent;
    while (ts.isParenthesizedExpression(gate)) gate = gate.parent;
    assert.ok(ts.isBinaryExpression(gate), 'the item sits behind a condition');
    return gate.left.getText(detailSource);
  };
  const runItem = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onRun');
  assert.equal(gateOf(runItem), '!isRunning && enabled');

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

test('Actions triggers stay stable and Configure has one schedule menu', () => {
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
  const panelTriggers = getMenuItems(
    getComponent(panelSource, 'ScheduledPrefillPlatformsPanel'),
    panelSource,
    'Button'
  ).filter((button) => getAttribute(button, panelSource, 'variant') === '"menu"');
  assert.equal(panelTriggers.length, 1, 'platform header has one schedule menu trigger');
  const [panelTrigger] = panelTriggers;
  assert.ok(panelTrigger, 'record group has a menu trigger');
  assert.equal(hasAttribute(panelTrigger, 'loading'), false, 'record trigger has no spinner');
  const footerTriggers = getMenuItems(
    getComponent(persistentCardSource, 'ScheduledPrefillPersistentCard'),
    persistentCardSource,
    'Button'
  ).filter((button) => getAttribute(button, persistentCardSource, 'variant') === '"menu"');
  assert.equal(footerTriggers.length, 0, 'persistent card does not repeat the Actions menu');
  assert.doesNotMatch(
    persistentCardSource.text,
    /scrollIntoView|scrollTo\s*\(/,
    'container actions never move the modal scroll position'
  );
  assert.equal(
    panelSource.text.includes('key={activeSchedule.id}'),
    false,
    'switching schedules updates the controlled section without forcing a remount'
  );

  const run = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onRun');
  const open = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onOpen');
  const enable = getItemWithCallback(row, detailSource, 'ActionMenuItem', 'onToggleEnabled');
  assert.ok(run);
  assert.ok(open);
  assert.ok(enable);
  // A menu already open when the row turns disabled keeps its portalled items mounted, so the
  // trigger's disabled state alone does not stop them firing. Every item repeats the rule, and
  // the two that own an extra pending flag add it to that shared one rather than replacing it.
  assert.equal(
    getAttribute(run, detailSource, 'disabled'),
    'actionsDisabled || runDisabled || runPending'
  );
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

test('container buttons retain the selected platform and schedule when moved out of Actions', () => {
  const section = findSoleNode(
    panelSource,
    'active platform section',
    (node) =>
      ts.isJsxSelfClosingElement(node) &&
      node.tagName.getText(panelSource) === 'ScheduledPrefillPlatformSection'
  );

  for (const [callbackName, input, expected] of [
    ['onSelectGames', undefined, ['steam', 'schedule-a']],
    ['onLogin', true, ['steam', true]],
    ['onLogout', undefined, ['steam']],
    ['onClearGames', undefined, ['steam', 'schedule-a']],
    ['onStop', undefined, ['steam']]
  ]) {
    const calls = [];
    bindLifted(getAttribute(section, panelSource, callbackName), {
      activeServiceKey: 'steam',
      activeSchedule: { id: 'schedule-a' },
      [callbackName]: (...args) => calls.push(args)
    })(input);
    assert.deepEqual(calls.pop(), expected);
  }

  const card = getComponent(persistentCardSource, 'ScheduledPrefillPersistentCard');
  const buttons = getMenuItems(card, persistentCardSource, 'Button');
  for (const callbackName of ['onSelectGames', 'onClearGames', 'onLogout', 'onStop']) {
    const button = buttons.find(
      (item) => getAttribute(item, persistentCardSource, 'onClick') === callbackName
    );
    assert.ok(button, `${callbackName} is available as a direct button`);
    assert.equal(getAttribute(button, persistentCardSource, 'type'), '"button"');
  }
});

test('an off schedule keeps its record row live and disables every control below it', () => {
  const platformSection = getComponent(platformSectionSource, 'ScheduledPrefillPlatformSection');
  const persistentCard = findSoleNode(
    platformSection,
    'persistent container card',
    (node) =>
      ts.isJsxSelfClosingElement(node) &&
      node.tagName.getText(platformSectionSource) === 'ScheduledPrefillPersistentCard'
  );
  assert.equal(getAttribute(persistentCard, platformSectionSource, 'disabled'), 'fieldsDisabled');

  const card = getComponent(persistentCardSource, 'ScheduledPrefillPersistentCard');
  const controls = findSoleNode(
    card,
    'persistent container control gate',
    (node) => ts.isJsxElement(node) && getTagName(node) === 'fieldset'
  );
  assert.equal(getAttribute(controls, persistentCardSource, 'disabled'), 'disabled');

  const settingsToggle = getMenuItems(card, persistentCardSource, 'Button').find(
    (button) =>
      hasAttribute(button, 'className') &&
      getAttribute(button, persistentCardSource, 'className') ===
        '"scheduled-prefill-persistent-card__settings-toggle"'
  );
  assert.ok(settingsToggle, 'shared container settings has a toggle');
  assert.equal(getAttribute(settingsToggle, persistentCardSource, 'disabled'), 'disabled');

  const panel = getComponent(panelSource, 'ScheduledPrefillPlatformsPanel');
  const recordTrigger = getMenuItems(panel, panelSource, 'Button').find(
    (button) => getAttribute(button, panelSource, 'variant') === '"menu"'
  );
  assert.ok(recordTrigger, 'record row keeps its Actions trigger');
  assert.equal(
    getAttribute(recordTrigger, panelSource, 'disabled'),
    'disabled',
    'record Actions follows only the modal-wide gate, not the schedule state'
  );

  assert.match(
    configModalSource.text,
    /containerSettings=\{\(containerDisabled\) =>[\s\S]*?disabled:\s*containerDisabled \|\|\s*!config[\s\S]*?disabled=\{containerDisabled \|\| clearingLogins\}/,
    'custom shared-setting controls receive the selected schedule gate explicitly'
  );
});

test('Actions cannot submit and the mobile modal contains touch scrolling', () => {
  assert.match(
    actionMenuSource.text,
    /export const ActionMenuItem[\s\S]*?return \(\s*<button\s+type="button"/,
    'regular menu items never submit an ancestor form'
  );
  assert.match(
    actionMenuSource.text,
    /export const ActionMenuDangerItem[\s\S]*?return \(\s*<button\s+type="button"/,
    'danger menu items never submit an ancestor form'
  );
  assert.match(
    schedulesCss,
    /\.scheduled-prefill-config-modal__viewport \.overflow-y-auto\s*\{\s*overscroll-behavior: contain;/,
    'touch scrolling cannot escape the modal or trigger pull-to-refresh'
  );
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
