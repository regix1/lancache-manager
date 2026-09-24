import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftConstArrow,
  loadNotificationModules,
  MemoryStorage,
  moduleUrl,
  operationRunRow,
  parseSource,
  prefillRunFields,
  pushRun
} from './transpile-module.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = resolve(__dirname, '..');
const read = (path) => readFileSync(resolve(WEB_ROOT, path), 'utf8');
const stripCss = read('src/components/common/CondensedNotificationStrip.css');
const itemCss = read('src/components/common/UnifiedNotificationItem.css');
const animationsCss = read('src/styles/utilities/animations.css');
const barComponent = read('src/components/common/UniversalNotificationBar.tsx');
const itemComponent = read('src/components/common/UnifiedNotificationItem.tsx');
const stripComponent = read('src/components/common/CondensedNotificationStrip.tsx');
const cancelModule = read('src/components/common/notificationCancel.ts');

test('pointer hover does not start a glow that opening immediately reverses', () => {
  assert.doesNotMatch(
    stripCss,
    /\.condensed-strip\s*:\s*hover[\s\S]*?\.condensed-strip-glow\s*\{[\s\S]*?opacity\s*:\s*1/
  );
  assert.match(
    stripCss,
    /\.condensed-strip-line\s*:\s*focus-visible\s+\.condensed-strip-glow\s*\{[\s\S]*?opacity\s*:\s*1/
  );
});

test('the notification surface keeps its border slot and limits transitions', () => {
  assert.match(barComponent, /\bborder-b\b/);
  assert.match(barComponent, /\bborder-transparent\b/);
  assert.match(barComponent, /transition-\[transform,opacity\]/);
  assert.doesNotMatch(barComponent, /bg-\[var\(--theme-nav-bg\)\]\s+transition\s+duration-300/);
});

test('notification cards keep stable render and action boundaries', () => {
  assert.match(itemComponent, /UnifiedNotificationItem\s*=\s*React\.memo\(/);
  assert.match(barComponent, /const handleDismiss = useCallback\(/);
  assert.match(barComponent, /const getCancelHandler = useCallback\(/);
  assert.doesNotMatch(barComponent, /onDismiss=\{\(\) => handleDismiss/);
  assert.doesNotMatch(barComponent, /onCancel=\{getCancelHandler\(notification\)\}/);
});

const barSource = parseSource(
  'src/components/common/UniversalNotificationBar.tsx',
  ts.ScriptKind.TSX
);
const stripSource = parseSource(
  'src/components/common/CondensedNotificationStrip.tsx',
  ts.ScriptKind.TSX
);
const exitSource = parseSource('src/hooks/useExitPresence.ts');
const constantsSource = parseSource('src/contexts/notifications/constants.ts');
const initializer = (source, name) =>
  findSoleNode(
    source,
    name,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  ).initializer.getText(source);
const exitFunction = findSoleNode(
  exitSource,
  'useExitPresence',
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'useExitPresence'
)
  .getText(exitSource)
  .replace(/^export\s+/, '');

// Commits settle layout work; passive work is deliberately a separate observable phase.
const mount = (source, name, extra = {}) => {
  const slots = [];
  const passive = new Map();
  const layout = new Map();
  const timers = new Map();
  let cursor = 0;
  let dirty = false;
  let tree;
  let props = {};
  let now = 0;
  let nextTimer = 0;
  const equal = (a, b) =>
    a !== undefined &&
    b !== undefined &&
    a.length === b.length &&
    a.every((value, index) => Object.is(value, b[index]));
  const useState = (initial) => {
    const index = cursor++;
    slots[index] ??= { value: typeof initial === 'function' ? initial() : initial };
    return [
      slots[index].value,
      (update) => {
        const value = typeof update === 'function' ? update(slots[index].value) : update;
        if (!Object.is(value, slots[index].value)) {
          slots[index].value = value;
          dirty = true;
        }
      }
    ];
  };
  const useRef = (initial) => {
    const index = cursor++;
    slots[index] ??= { current: initial };
    return slots[index];
  };
  const effect = (queue) => (setup, deps) => {
    const index = cursor++;
    const slot = (slots[index] ??= {});
    if (!equal(slot.deps, deps)) {
      slot.deps = deps;
      queue.set(index, setup);
    }
  };
  const useCallback = (callback, deps) => {
    const index = cursor++;
    const slot = (slots[index] ??= {});
    if (!equal(slot.deps, deps)) Object.assign(slot, { deps, callback });
    return slot.callback;
  };
  const target = () => {
    const listeners = new Map();
    return {
      listeners,
      addEventListener: (name, callback) => {
        if (!listeners.has(name)) listeners.set(name, new Set());
        listeners.get(name).add(callback);
      },
      removeEventListener: (name, callback) => listeners.get(name)?.delete(callback),
      dispatch: (name, event) =>
        [...(listeners.get(name) ?? [])].forEach((callback) => callback(event))
    };
  };
  const setTimeout = (callback, delay) => {
    const id = ++nextTimer;
    timers.set(id, { callback, due: now + delay });
    return id;
  };
  const clearTimeout = (id) => timers.delete(id);
  const setInterval = (callback, delay) => {
    const id = setTimeout(callback, delay);
    timers.get(id).interval = delay;
    return id;
  };
  const window = {
    ...target(),
    setTimeout,
    clearTimeout,
    setInterval,
    clearInterval: clearTimeout
  };
  const document = { ...target(), documentElement: target(), visibilityState: 'visible' };
  class Node {
    constructor(parent = null) {
      this.parent = parent;
    }
    contains(node) {
      for (let current = node; current; current = current.parent) if (current === this) return true;
      return false;
    }
  }
  const host = new Node();
  host.hovered = false;
  host.matches = () => host.hovered;
  const trigger = new Node(host);
  const action = new Node(host);
  const outside = new Node();
  document.body = outside;
  document.activeElement = outside;
  document.hit = outside;
  document.elementFromPoint = () => document.hit;
  trigger.focusCalls = [];
  trigger.focus = (options) => {
    trigger.focusCalls.push(options);
    document.activeElement = trigger;
  };
  host.querySelector = () => trigger;
  const React = {
    createElement: (type, attributes, ...children) => {
      children = children.flat(Infinity);
      const keys = children.filter((child) => child?.key != null).map((child) => child.key);
      assert.equal(
        new Set(keys).size,
        keys.length,
        'sibling keys stay unique before layout effects'
      );
      return {
        type,
        key: attributes?.key ?? null,
        props: { ...attributes, children }
      };
    }
  };
  const bindings = {
    React,
    useState,
    useRef,
    useCallback,
    useEffect: effect(passive),
    useLayoutEffect: effect(layout),
    window,
    document,
    setTimeout,
    clearTimeout,
    Node,
    Date: { now: () => now },
    ...extra
  };
  bindings.useExitPresence = bindLifted(exitFunction, bindings);
  const component = bindLifted(initializer(source, name), bindings, { jsx: ts.JsxEmit.React });
  const run = (queue) => {
    const pending = [...queue];
    queue.clear();
    for (const [index, setup] of pending) {
      slots[index].cleanup?.();
      slots[index].cleanup = setup();
    }
  };
  const commit = () => {
    let passes = 0;
    do {
      assert.ok(++passes < 30, 'layout updates settle');
      dirty = false;
      cursor = 0;
      tree = component(props);
      if (tree?.props.ref) tree.props.ref.current = host;
      run(layout);
    } while (dirty);
    return tree;
  };
  const flushPassive = () => {
    let passes = 0;
    while (passive.size || dirty) {
      assert.ok(++passes < 30, 'passive updates settle');
      run(passive);
      if (dirty) commit();
    }
    return tree;
  };
  return {
    get tree() {
      return tree;
    },
    window,
    document,
    timers,
    host,
    trigger,
    action,
    outside,
    render(next = props) {
      flushPassive();
      props = next;
      return commit();
    },
    event(callback) {
      flushPassive();
      callback();
      return commit();
    },
    flushPassive,
    advance(ms) {
      flushPassive();
      const end = now + ms;
      for (;;) {
        const next = [...timers].sort((a, b) => a[1].due - b[1].due || a[0] - b[0])[0];
        if (!next || next[1].due > end) break;
        now = next[1].due;
        if (next[1].interval) next[1].due += next[1].interval;
        else timers.delete(next[0]);
        next[1].callback();
        if (dirty) commit();
        flushPassive();
      }
      now = end;
      return tree;
    },
    dispose() {
      flushPassive();
      slots.forEach((slot) => slot.cleanup?.());
      passive.clear();
      layout.clear();
    }
  };
};

const elements = (tree) =>
  !tree || typeof tree !== 'object'
    ? []
    : [tree, ...(tree.props?.children ?? []).flatMap(elements)];
const panel = (tree) =>
  elements(tree).find((node) => node.props.className?.split(' ').includes('condensed-strip-panel'));
const button = (tree) => elements(tree).find((node) => node.type === 'button');
const notice = (id = 'one') => ({
  id,
  type: 'game_detection',
  status: 'running',
  message: 'Detecting games',
  startedAt: new Date(0),
  progress: 10
});
const segment = (key = 'one') => ({
  key,
  notification: notice(key),
  variant: 'success'
});

const { isTerminalNotificationStatus } = await import(
  await compileToUrl('../src/contexts/notifications/notificationStatus.ts')
);
// The real variant function, so the bar and card tests read the class a card is drawn with.
const cancelSource = parseSource('src/components/common/notificationCancel.ts');
const { VARIANT_BY_STATUS } = await import(await compileToUrl('../src/utils/statusVariant.ts'));
const getNotificationVariant = bindLifted(initializer(cancelSource, 'getNotificationVariant'), {
  VARIANT_BY_STATUS
});
// A plain element factory for components lifted outside the mount harness.
const jsx = { jsx: ts.JsxEmit.React };
const h = {
  createElement: (type, attributes, ...children) => ({
    type,
    key: attributes?.key ?? null,
    props: { ...attributes, children: children.flat(Infinity) }
  }),
  Fragment: 'Fragment'
};

const makeBar = (
  initial = [],
  displayModes = {},
  typeToServiceKey = {},
  {
    defaultMode = 'full',
    ready = true,
    isMobile = false,
    remove,
    hide,
    lost = false,
    closeOperation,
    toasts = []
  } = {}
) => {
  const context = {
    notifications: initial,
    removeNotification: remove ?? (() => assert.fail('rendering must not remove notifications')),
    hideNotification: hide ?? (() => assert.fail('rendering must not hide notifications')),
    updateNotification: () => assert.fail('rendering must not update notifications')
  };
  const runner = mount(barSource, 'UniversalNotificationBar', {
    useNotifications: () => context,
    useConnectionLost: () => lost,
    themeService: { getDisableStickyNotificationsSync: () => false },
    useScheduleDisplayModes: () => ({ modes: displayModes, defaultMode, ready }),
    useMediaQuery: (query) => (query === '(max-width: 767px)' ? isMobile : false),
    APP_EVENTS: { STICKY_NOTIFICATIONS_CHANGE: 'sticky', NOTIFICATION_REMOVING: 'removing' },
    NOTIFICATION_ANIMATION_DURATION_MS: bindLifted(
      `() => (${initializer(constantsSource, 'NOTIFICATION_ANIMATION_DURATION_MS')})`,
      {}
    )(),
    CANCEL_CONFIG_BY_TYPE: {},
    SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY: typeToServiceKey,
    MOBILE_FULL_CARD_CAP: bindLifted(
      `() => (${initializer(constantsSource, 'MOBILE_FULL_CARD_CAP')})`,
      {}
    )(),
    isTerminalNotificationStatus,
    platformDisplayModeKey: (service, platform) => `${service}:${platform}`,
    getNotificationVariant,
    handleCancel: () => {
      assert.fail('rendering must not cancel work');
    },
    ApiService: {
      closeOperation: closeOperation ?? (() => assert.fail('this card closes nothing'))
    },
    notifyToastError: (message, error) => toasts.push([message, error.message]),
    getErrorMessage: (error) => error.message,
    i18n: { t: (key) => key },
    CondensedNotificationStrip: 'CondensedNotificationStrip',
    BackgroundTaskControls: 'BackgroundTaskControls',
    UnifiedNotificationItem: 'UnifiedNotificationItem',
    CustomScrollbar: 'CustomScrollbar'
  });
  return Object.assign(runner, {
    setNotifications(notifications) {
      return runner.event(() => {
        context.notifications = notifications;
      });
    }
  });
};

/** Where the bar draws a card: 'condensed' inside the strip's panel, 'full' outside it. */
const placement = (tree, id) => {
  const holds = (root) =>
    elements(root).some(
      (node) => node.type === 'UnifiedNotificationItem' && node.props.notification.id === id
    );
  assert.ok(holds(tree), `${id} is drawn`);
  const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
  return holds(strip) ? 'condensed' : 'full';
};

const loadRunModules = () =>
  loadNotificationModules(
    moduleUrl("export default { t: (key) => key, exists: (key) => key.startsWith('signalr.') };")
  );

test('silent mapping refreshes stay inside the compact strip under the Compact default through every row and event', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const modules = await loadRunModules();
  const full = { ...notice('prefill'), type: 'scheduled_prefill' };
  const compact = notice('detection');
  let state = modules.createRunStoreState();
  let notifications = [full, compact];
  // The mapping schedules have no style of their own, so the global default places them. [63]
  const bar = makeBar(
    notifications,
    { gameDetection: 'condensed', scheduledPrefill: 'full' },
    modules.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY,
    { defaultMode: 'condensed' }
  );
  bar.render();
  let commits = 0;
  const draw = () => {
    notifications = [full, compact, ...modules.deriveNotifications(state, [])];
    const tree = bar.setNotifications(notifications);
    const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
    const controls = elements(tree).filter((node) => node.type === 'BackgroundTaskControls');
    assert.deepEqual(
      controls,
      elements(strip).filter((node) => node.type === 'BackgroundTaskControls'),
      'a refresh must not insert a separate background row above the full cards'
    );
    assert.equal(notifications[0], full);
    assert.equal(notifications[1], compact);
    commits += 1;
  };
  const dispatch = (operationId, build, source) => {
    state = modules.applyDetail(state, operationId, build, source, { requestSeq: 0 });
    draw();
  };

  for (const [type, operationType] of [
    ['riot_game_mapping', 'riotMapping'],
    ['battle_net_game_mapping', 'battleNetMapping']
  ]) {
    const entry = modules.NOTIFICATION_REGISTRY.find((item) => item.type === type);
    const operationId = `${type}-refresh`;
    const fields = {
      operationId,
      context: { processed: 1, total: 1, mapped: 0 }
    };
    // A silent automatic refresh: a background row.
    const background = { operationType, name: operationType, visibility: 'background' };
    state = pushRun(modules, state, operationRunRow(operationId, background));
    draw();
    modules.buildStartedHandler(entry.started, dispatch)(fields);
    modules.buildProgressHandler(
      entry,
      entry.progress,
      dispatch
    )({ ...fields, status: 'running', percentComplete: 90 });
    assert.equal(notifications.find((card) => card.type === type)?.controlOnly, true);
    const ending = type === 'battle_net_game_mapping' ? 'skipped' : 'completed';
    modules.buildCompleteHandler(
      entry,
      entry.complete,
      dispatch
    )({ ...fields, success: true, status: ending });
    state = pushRun(
      modules,
      state,
      operationRunRow(operationId, { ...background, status: ending, percentComplete: 100 })
    );
    draw();
    assert.equal(
      notifications.some((card) => card.type === type),
      false
    );
  }
  assert.equal(commits, 10);
  bar.dispose();
});

test('background controls follow their schedule style, then the global default', () => {
  for (const status of ['waiting', 'running', 'cancelling']) {
    for (const mode of [undefined, 'condensed', 'full']) {
      for (const defaultMode of ['full', 'condensed']) {
        const card = { ...notice(), controlOnly: true, status };
        const bar = makeBar(
          [card],
          mode ? { gameDetection: mode } : {},
          { game_detection: 'gameDetection' },
          { defaultMode }
        );
        const tree = bar.render();
        const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
        assert.equal(
          elements(strip).some((node) => node.type === 'BackgroundTaskControls'),
          (mode ?? defaultMode) === 'condensed',
          `${status} controls with ${mode ?? 'no'} display preference, default ${defaultMode}`
        );
        assert.equal(
          elements(tree).filter((node) => node.type === 'BackgroundTaskControls').length,
          1
        );
        bar.dispose();
      }
    }
  }
});

test('normal notifications and terminal failures retain the full-view default', () => {
  for (const card of [notice(), { ...notice(), controlOnly: true, status: 'failed' }]) {
    const bar = makeBar([card]);
    const tree = bar.render();
    assert.equal(placement(tree, card.id), 'full');
    bar.dispose();
  }
});

test('per-platform background tasks retain their full-view default', () => {
  const card = {
    ...notice('operation-prefill'),
    type: 'scheduled_prefill',
    controlOnly: true,
    details: { service: 'Steam', operationId: 'prefill' }
  };
  const bar = makeBar(
    [card],
    { 'scheduledPrefill:Steam': 'full' },
    { scheduled_prefill: 'scheduledPrefill' },
    { defaultMode: 'condensed' }
  );
  const tree = bar.render();
  const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
  assert.equal(
    elements(strip).some((node) => node.type === 'BackgroundTaskControls'),
    false
  );
  assert.equal(
    elements(tree).some((node) => node.type === 'BackgroundTaskControls'),
    true
  );
  bar.dispose();
});

test('one global default decides every notification a schedule does not style', () => {
  const keys = {
    game_detection: 'gameDetection',
    scheduled_prefill: 'scheduledPrefill',
    epic_catalog_update: 'epicMapping'
  };
  const card = (id, fields = {}) => ({ ...notice(id), ...fields });
  const keyedPopup = {
    type: 'generic',
    status: 'failed',
    details: { notificationType: 'error', serviceKey: 'gameDetection' }
  };
  const byDefault = (defaultMode) => defaultMode;
  const cases = [
    ['a removal', card('removal', { type: 'game_removal' }), {}, byDefault],
    ['log processing', card('logs', { type: 'log_processing' }), {}, byDefault],
    ['a bulk removal', card('bulk', { type: 'bulk_removal' }), {}, byDefault],
    ['a sign-in', card('login', { type: 'prefill_login' }), {}, byDefault],
    ['a scheduled run with no style', card('detect'), {}, byDefault],
    ['a scheduled run styled Normal', card('detect'), { gameDetection: 'full' }, () => 'full'],
    [
      'a scheduled run styled Compact',
      card('detect'),
      { gameDetection: 'condensed' },
      () => 'condensed'
    ],
    // Popups that are not runs follow the same two rules. [63]
    [
      "an error that names a schedule follows that schedule's Compact style",
      card('keyed', keyedPopup),
      { gameDetection: 'condensed' },
      () => 'condensed'
    ],
    [
      "an error that names a schedule follows that schedule's Normal style",
      card('keyed', keyedPopup),
      { gameDetection: 'full' },
      () => 'full'
    ],
    ['an error that names a schedule with no style', card('keyed', keyedPopup), {}, byDefault],
    [
      'a popup that belongs to no schedule',
      card('saved', {
        type: 'generic',
        status: 'completed',
        details: { notificationType: 'success' }
      }),
      {},
      byDefault
    ],
    [
      'an error popup that belongs to no schedule',
      card('error', {
        type: 'generic',
        status: 'failed',
        details: { notificationType: 'error' }
      }),
      {},
      byDefault
    ],
    [
      'the dropped Steam session error, which belongs to no schedule',
      card('session', { type: 'steam_session_error', status: 'failed' }),
      {},
      byDefault
    ],
    [
      'an Epic catalog announcement with no mapping style',
      card('catalog', { type: 'epic_catalog_update', status: 'completed' }),
      {},
      byDefault
    ],
    ['a background row with no style', card('row', { controlOnly: true }), {}, byDefault],
    [
      'a per-platform prefill background row with no style',
      card('platform', {
        type: 'scheduled_prefill',
        controlOnly: true,
        details: { service: 'Steam' }
      }),
      {},
      byDefault
    ]
  ];
  for (const defaultMode of ['full', 'condensed']) {
    for (const [label, notification, modes, expected] of cases) {
      const bar = makeBar([notification], modes, keys, { defaultMode });
      assert.equal(
        placement(bar.render(), notification.id),
        expected(defaultMode),
        `${label}, global default ${defaultMode}`
      );
      bar.dispose();
    }
  }
});

test('after a reload a run card waits for the styles while a popup the browser owns draws at once', () => {
  const keys = { game_detection: 'gameDetection', epic_catalog_update: 'epicMapping' };
  const run = { ...notice('detect'), details: { operationId: 'detect' } };
  const popup = {
    ...notice('saved'),
    type: 'generic',
    status: 'completed',
    details: { notificationType: 'success' }
  };
  const browserCards = [
    popup,
    {
      ...notice('keyed'),
      type: 'generic',
      status: 'failed',
      details: { notificationType: 'error', serviceKey: 'gameDetection' }
    },
    { ...notice('session'), type: 'steam_session_error', status: 'failed' },
    { ...notice('catalog'), type: 'epic_catalog_update', status: 'completed' }
  ];
  const drawn = (tree, id) =>
    elements(tree).some(
      (node) => node.type === 'UnifiedNotificationItem' && node.props.notification.id === id
    );

  // Before the first settings read settles the hook reports no styles and its initial full
  // default, so a popup's 5 s runs while it is on screen as a full card. [63]
  const loading = makeBar([run, ...browserCards], {}, keys, { ready: false });
  const first = loading.render();
  assert.equal(drawn(first, 'detect'), false, 'no run card before the styles are known');
  for (const card of browserCards) {
    assert.equal(placement(first, card.id), 'full', `${card.id} draws before the styles load`);
  }
  loading.dispose();

  const known = makeBar([run, ...browserCards], { gameDetection: 'condensed' }, keys, {
    defaultMode: 'condensed'
  });
  const settled = known.render();
  for (const card of [run, ...browserCards]) {
    assert.equal(placement(settled, card.id), 'condensed', `${card.id} once the styles load`);
  }
  known.dispose();
});

test('on a phone at most three full cards show and the rest go to the strip', () => {
  const cards = ['a', 'b', 'c', 'd', 'e'].map((id, index) => ({
    ...notice(id),
    type: 'game_removal',
    startedAt: new Date(index)
  }));
  const phone = makeBar(cards, {}, {}, { isMobile: true });
  const phoneTree = phone.render();
  assert.deepEqual(
    cards.map((card) => placement(phoneTree, card.id)),
    ['full', 'full', 'full', 'condensed', 'condensed']
  );
  assert.equal(
    elements(phoneTree).find((node) => node.type === 'CustomScrollbar').props.maxHeight,
    '12rem'
  );
  phone.dispose();

  const desktop = makeBar(cards);
  const desktopTree = desktop.render();
  assert.deepEqual(
    cards.map((card) => placement(desktopTree, card.id)),
    ['full', 'full', 'full', 'full', 'full']
  );
  // The floating panel is bounded at every size, so a tall list scrolls inside it.
  assert.equal(
    elements(desktopTree).find((node) => node.type === 'CustomScrollbar').props.maxHeight,
    '70vh'
  );
  desktop.dispose();
});

test('a warning ending colors its strip segment amber', () => {
  const warning = {
    ...notice('scan'),
    type: 'eviction_scan',
    status: 'completed',
    detailMessage: 'Game detection failed: disk unavailable',
    details: { operationId: 'scan', notificationType: 'warning' }
  };
  const bar = makeBar([warning], {}, {}, { defaultMode: 'condensed' });
  const strip = elements(bar.render()).find((node) => node.type === 'CondensedNotificationStrip');
  assert.deepEqual(
    strip.props.segments.map((item) => item.variant),
    ['warning']
  );
  bar.dispose();

  const StripSegment = bindLifted(
    initializer(stripSource, 'StripSegment'),
    { React: h, isTerminalNotificationStatus },
    jsx
  );
  const drawn = StripSegment({ segment: strip.props.segments[0] });
  assert.match(drawn.props.className, /\bnotification-status--warning\b/);
  assert.deepEqual(Object.keys(drawn.props.style), ['--seg-fill']);
});

// ── Closing a kept ending [49] ──────────────────────────────────────────────

const settle = () => new Promise((resolve) => setImmediate(resolve));
const dismissBar = (card, closeOperation, lost = false) => {
  const removed = [];
  const hidden = [];
  const closeCalls = [];
  const toasts = [];
  const bar = makeBar(
    [card],
    {},
    {},
    {
      remove: (...args) => removed.push(args),
      hide: (...args) => hidden.push(args),
      lost,
      closeOperation: (id) => {
        closeCalls.push(id);
        return closeOperation(id);
      },
      toasts
    }
  );
  bar.render();
  const item = () =>
    elements(bar.tree).find(
      (node) => node.type === 'UnifiedNotificationItem' && node.props.notification.id === card.id
    );
  const dismiss = () => bar.event(() => item().props.onDismiss(card.id));
  return { bar, removed, hidden, closeCalls, toasts, item, dismiss };
};

test('closing a running card while the server is unreachable hides it at once and removes nothing', () => {
  const running = {
    ...notice('card-1'),
    type: 'eviction_scan',
    details: { operationId: 'op-1', operationIds: ['op-1'] }
  };
  const { bar, removed, hidden, closeCalls, toasts, item, dismiss } = dismissBar(
    running,
    () => assert.fail('a running card closes nothing on the server'),
    true
  );
  try {
    dismiss();
    const atClick = { hidden: [...hidden], fading: item().props.isAnimatingOut };
    bar.advance(1000);
    assert.deepEqual(removed, [], 'the run stays tracked');
    assert.deepEqual(atClick, { hidden: [['card-1']], fading: false }, 'hidden in the click');
    assert.deepEqual(hidden, [['card-1']]);
    assert.deepEqual(closeCalls, []);
    assert.deepEqual(toasts, []);
    assert.equal(item().props.connectionLost, true, 'the bar hands the card the connection state');
  } finally {
    bar.dispose();
  }
});

test('closing a kept card asks the server once and removes it only after the answer', async () => {
  let release;
  const kept = {
    ...notice('card-1'),
    type: 'eviction_scan',
    status: 'failed',
    details: { operationId: 'op-1', closeOperationIds: ['op-1'] }
  };
  const { bar, removed, closeCalls, toasts, item, dismiss } = dismissBar(
    kept,
    () =>
      new Promise((resolve) => {
        release = resolve;
      })
  );
  try {
    dismiss();
    bar.advance(1000);
    assert.deepEqual(closeCalls, ['op-1']);
    assert.deepEqual(removed, [], 'the card stays until the server answered');
    assert.equal(item().props.isAnimatingOut, false);
    release();
    await settle();
    bar.advance(299);
    assert.equal(item().props.isAnimatingOut, true);
    assert.deepEqual(removed, []);
    bar.advance(1);
    assert.deepEqual(removed, [['card-1', ['op-1']]]);
    assert.deepEqual(closeCalls, ['op-1']);
    assert.deepEqual(toasts, []);
  } finally {
    bar.dispose();
  }
});

test('a kept card whose close fails stays on screen with one error toast', async () => {
  const kept = {
    ...notice('card-1'),
    type: 'eviction_scan',
    status: 'failed',
    details: { operationId: 'op-1', closeOperationIds: ['op-1'] }
  };
  const { bar, removed, toasts, item, dismiss } = dismissBar(kept, () =>
    Promise.reject(new Error('Server unreachable'))
  );
  try {
    dismiss();
    await settle();
    bar.advance(1000);
    assert.deepEqual(toasts, [['common.notifications.closeOperationFailed', 'Server unreachable']]);
    assert.deepEqual(removed, []);
    assert.equal(item().props.isAnimatingOut, false);
  } finally {
    bar.dispose();
  }
});

test('a bulk card closes every kept run it lists and passes on only the confirmed ones', async () => {
  const bulk = {
    ...notice('bulk'),
    type: 'bulk_removal',
    status: 'failed',
    details: { closeOperationIds: ['kept-a', 'kept-b'] }
  };
  const { bar, removed, closeCalls, toasts, dismiss } = dismissBar(bulk, (id) =>
    id === 'kept-a' ? Promise.resolve() : Promise.reject(new Error('Server unreachable'))
  );
  try {
    dismiss();
    await settle();
    bar.advance(300);
    assert.deepEqual(closeCalls, ['kept-a', 'kept-b']);
    assert.deepEqual(removed, [['bulk', ['kept-a']]]);
    assert.deepEqual(toasts, [['common.notifications.closeOperationFailed', 'Server unreachable']]);
  } finally {
    bar.dispose();
  }
});

test('a card with nothing kept on the server closes here without a request', async () => {
  const plain = { ...notice('plain'), status: 'completed', details: { operationId: 'op-2' } };
  const { bar, removed, closeCalls, dismiss } = dismissBar(plain, () =>
    assert.fail('nothing to close')
  );
  try {
    dismiss();
    await settle();
    bar.advance(300);
    assert.deepEqual(closeCalls, []);
    assert.deepEqual(removed, [['plain', undefined]]);
  } finally {
    bar.dispose();
  }
});

const makeStrip = (canHover = false) => {
  const preference = { reduced: false };
  const constants = Object.fromEntries(
    ['PANEL_EXIT_MS', 'LINE_EXIT_MS', 'SEG_EXIT_MS', 'HOVER_OPEN_DELAY_MS'].map((name) => [
      name,
      bindLifted(`() => (${initializer(stripSource, name)})`, {})()
    ])
  );
  const t = (key) => key;
  const runner = mount(stripSource, 'CondensedNotificationStrip', {
    ...constants,
    useTranslation: () => ({ t }),
    useMediaQuery: () => preference.reduced,
    StripSegment: 'StripSegment'
  });
  const props = {
    segments: [segment()],
    canHover,
    children: 'cards'
  };
  return Object.assign(runner, {
    preference,
    props,
    start() {
      return runner.render(props);
    },
    toggle(detail = 1) {
      return runner.event(() =>
        button(runner.tree).props.onClick({
          detail,
          target: runner.trigger,
          currentTarget: runner.trigger
        })
      );
    },
    focus(next) {
      return runner.event(() => {
        const previous = runner.document.activeElement;
        runner.document.activeElement = next;
        if (runner.host.contains(previous))
          runner.tree?.props.onBlur?.({
            target: previous,
            currentTarget: runner.host,
            relatedTarget: next
          });
      });
    },
    key(key, target = runner.document.activeElement) {
      return runner.event(() =>
        runner.document.dispatch('keydown', { key, target, currentTarget: runner.document })
      );
    },
    pointer(target = runner.outside) {
      return runner.event(() => runner.document.dispatch('pointerdown', { target }));
    },
    enter(pointerMoved = true) {
      return runner.event(() => {
        runner.host.hovered = true;
        runner.document.hit = runner.trigger;
        runner.tree.props.onMouseEnter?.();
        if (pointerMoved)
          runner.document.dispatch('pointermove', {
            clientX: 2,
            clientY: 3,
            target: runner.trigger
          });
      });
    },
    /** A pointer move landing on `target`, with no mouseleave (a panel shrank under it). */
    move(target) {
      return runner.event(() => {
        runner.host.hovered = runner.host.contains(target);
        runner.document.hit = target;
        runner.document.dispatch('pointermove', { clientX: 2, clientY: 90, target });
      });
    },
    leave(documentBoundary = false) {
      return runner.event(() => {
        runner.host.hovered = false;
        runner.document.hit = runner.outside;
        if (documentBoundary) runner.document.documentElement.dispatch('mouseleave', {});
        else runner.tree.props.onMouseLeave();
      });
    },
    segments(segments) {
      props.segments = segments;
      return runner.render(props);
    },
    reduced(value) {
      return runner.event(() => {
        preference.reduced = value;
      });
    }
  });
};

test('a live bar renders on its first commit before passive effects', () => {
  for (const initial of [[], [notice()]]) {
    const runner = makeBar(initial);
    const tree = initial.length
      ? runner.render()
      : (runner.render(), runner.setNotifications([notice()]));
    try {
      assert.ok(tree, 'a live notification must return a bar before passive effects');
    } finally {
      runner.dispose();
    }
  }
});

test('refill masks active bar exit styles before passive effects', () => {
  const runner = makeBar([notice()]);
  try {
    runner.render();
    runner.flushPassive();
    const surface = () => runner.tree.props.children[0].props;
    runner.setNotifications([]);
    runner.advance(299);
    assert.doesNotMatch(surface().className, /-translate-y-full|opacity-0/);
    runner.advance(1);
    assert.match(surface().className, / -translate-y-full opacity-0/);
    assert.equal(surface().style, undefined, 'the exit is the Tailwind utilities, not a style');
    const tree = runner.setNotifications([notice()]);
    assert.doesNotMatch(
      tree.props.children[0].props.className,
      /-translate-y-full|opacity-0/,
      'live refill must mask both stale exit properties'
    );
    runner.advance(1000);
    assert.ok(runner.tree);
  } finally {
    runner.dispose();
  }
});

test('bar hold refill cancels removal and uninterrupted empty exits at 600 ms', () => {
  const runner = makeBar([notice()]);
  try {
    runner.render();
    runner.flushPassive();
    runner.setNotifications([]);
    runner.advance(150);
    runner.setNotifications([notice()]);
    runner.advance(1000);
    assert.ok(runner.tree);
    runner.setNotifications([]);
    runner.advance(599);
    assert.ok(runner.tree);
    runner.advance(1);
    assert.equal(runner.tree, null);
  } finally {
    runner.dispose();
  }
});

test('first compact close retains a closing inert panel until expiry', () => {
  const runner = makeStrip();
  try {
    runner.start();
    assert.ok(panel(runner.toggle()));
    runner.flushPassive();
    const closing = panel(runner.toggle());
    assert.ok(closing, 'the first close commit must retain the panel');
    assert.match(closing.props.className, /is-closing/);
    assert.equal(closing.props.inert, true);
    runner.advance(149);
    assert.ok(panel(runner.tree));
    runner.advance(1);
    assert.equal(panel(runner.tree), undefined);
  } finally {
    runner.dispose();
  }
});

test('compact reopen immediately clears closing semantics and cancels old expiry', () => {
  const runner = makeStrip();
  try {
    runner.start();
    runner.toggle();
    runner.toggle();
    runner.advance(30);
    const reopened = panel(runner.toggle());
    assert.ok(reopened);
    assert.doesNotMatch(reopened.props.className, /is-closing/);
    assert.ok(!reopened.props.inert);
    runner.advance(200);
    assert.ok(panel(runner.tree));
    runner.event(() => runner.document.dispatch('keydown', { key: 'Escape' }));
    assert.match(panel(runner.tree).props.className, /is-closing/);
  } finally {
    runner.dispose();
  }
});

test('reduced motion closes immediately and cannot revive cancelled closing presence', () => {
  const runner = makeStrip();
  try {
    runner.start();
    runner.toggle();
    runner.reduced(true);
    runner.toggle();
    assert.equal(panel(runner.tree), undefined);
    runner.reduced(false);
    assert.equal(panel(runner.tree), undefined);
    runner.toggle();
    runner.toggle();
    runner.advance(30);
    runner.reduced(true);
    assert.equal(panel(runner.tree), undefined);
    runner.reduced(false);
    assert.equal(panel(runner.tree), undefined);
    assert.ok(panel(runner.toggle()));
  } finally {
    runner.dispose();
  }
});

test('segment loss suppresses open or closing panels without reviving them on refill', () => {
  for (const closing of [false, true]) {
    const runner = makeStrip();
    try {
      runner.start();
      runner.toggle();
      if (closing) {
        runner.toggle();
        runner.advance(30);
      }
      runner.segments([]);
      assert.equal(panel(runner.tree), undefined);
      assert.match(runner.tree.props.className, /is-vanishing/);
      runner.advance(30);
      runner.segments([segment()]);
      assert.equal(panel(runner.tree), undefined);
      assert.doesNotMatch(runner.tree.props.className, /is-vanishing/);
      assert.ok(panel(runner.toggle()));
      runner.segments([]);
      runner.advance(449);
      assert.ok(runner.tree);
      runner.advance(1);
      assert.equal(runner.tree, null);
    } finally {
      runner.dispose();
    }
  }
});

test('progress retains disclosure and departing segment ghosts settle before paint', () => {
  const runner = makeStrip();
  const segments = () => elements(runner.tree).filter((node) => node.type === 'StripSegment');
  try {
    runner.start();
    runner.segments([segment('one'), segment('two')]);
    runner.toggle();
    runner.segments([
      { ...segment('one'), notification: { ...notice(), progress: 45 } },
      segment('two')
    ]);
    assert.ok(panel(runner.tree));
    runner.segments([segment('two')]);
    assert.deepEqual(
      segments().map((node) => [node.key, node.props.leaving]),
      [
        ['one', true],
        ['two', false]
      ]
    );
    runner.segments([segment('one'), segment('two')]);
    assert.ok(segments().every((node) => !node.props.leaving));
    runner.segments([segment('two')]);
    runner.advance(450);
    assert.deepEqual(
      segments().map((node) => node.key),
      ['two']
    );
    runner.toggle();
    runner.flushPassive();
    assert.ok(runner.timers.size > 0);
  } finally {
    runner.dispose();
  }
  assert.equal(runner.timers.size, 0);
  assert.ok([...runner.document.listeners.values()].every((listeners) => listeners.size === 0));
});

test('rapid background task turnover reclaims its strip segment without duplicate keys', () => {
  const runner = makeStrip();
  try {
    runner.start();
    for (let iteration = 0; iteration < 8; iteration += 1) {
      runner.segments([segment('one'), segment('background-controls')]);
      const segments = elements(runner.tree).filter((node) => node.type === 'StripSegment');
      assert.deepEqual(
        segments.map((node) => [node.key, node.props.leaving]),
        [
          ['one', false],
          ['background-controls', false]
        ]
      );
      runner.segments([segment('one')]);
      runner.advance(40);
    }
    runner.advance(450);
    assert.deepEqual(
      elements(runner.tree)
        .filter((node) => node.type === 'StripSegment')
        .map((node) => node.key),
      ['one']
    );
  } finally {
    runner.dispose();
  }
});

test('full and revealed cards reuse the background base radius while the line stays square', () => {
  const root = itemComponent.match(/ flex items-start sm:items-center[^`]*`/)[0];
  assert.match(root, /\brounded\s/, 'normal card root must use the existing base radius');
  assert.doesNotMatch(root, /rounded-lg/);
  assert.match(itemComponent, /background-task-control-row[^\n]*\brounded\b/);
  const background = read('src/components/common/BackgroundTaskControls.css');
  assert.match(background, /border-radius:\s*var\(--theme-border-radius\)/);
  assert.match(stripCss, /\.condensed-strip-line\s*\{[^}]*border-radius:\s*0/);
});

// ── One status -> color map, no inline styles, the design floor (criteria 37, 38) ──

/** The declarations of the rule whose selector is exactly `selector`. */
const ruleBody = (css, selector) => {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`(?:^|\\n)\\s*${escaped}\\s*\\{([^}]*)\\}`));
  assert.ok(match, `${selector} rule exists`);
  return match[1];
};

test('card and strip read one status color map written once', () => {
  const variants = ['success', 'error', 'warning', 'info', 'waiting', 'neutral'];
  const allCss = [stripCss, itemCss, animationsCss].join('\n');
  for (const variant of variants) {
    const selector = `.notification-status--${variant}`;
    assert.equal(
      allCss.split(`${selector} {`).length - 1,
      1,
      `${selector} is written once, in animations.css`
    );
    const body = ruleBody(animationsCss, selector);
    assert.match(body, /--notification-status-color:\s*var\(--theme-[a-z-]+\)/);
    assert.match(body, /--notification-status-glow:\s*var\(--theme-[a-z-]+\)/);
  }
  assert.match(
    ruleBody(itemCss, '.notification-card'),
    /border-left:\s*3px solid var\(--notification-status-color\)/
  );
  assert.match(
    ruleBody(itemCss, '.notification-card__icon'),
    /color:\s*var\(--notification-status-color\)/
  );
  assert.match(
    ruleBody(animationsCss, '.notification-progress-fill'),
    /width:\s*var\(--progress-width\)/
  );
  for (const selector of ['.notification-progress-fill', '.notification-progress-indeterminate'])
    assert.match(ruleBody(animationsCss, selector), /var\(--notification-status-color/);
  assert.match(ruleBody(stripCss, '.condensed-strip-glow-seg'), /--notification-status-glow/);
  for (const source of [stripCss, stripComponent, itemComponent, barComponent, cancelModule]) {
    assert.doesNotMatch(source, /--seg-color|--seg-glow-color|--notification-progress-color/);
    assert.doesNotMatch(source, /GLOW_COLOR_BY_STATUS_COLOR|STATUS_COLOR_BY_VARIANT/);
    assert.doesNotMatch(source, /condensed-strip-seg--|notification-card--/);
    assert.doesNotMatch(source, /color-mix\(/);
  }
});

test('the three notification views set no inline style but the two fill widths', () => {
  const styleLines = (source) =>
    source
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => /\bstyle=/.test(line));
  assert.deepEqual(styleLines(barComponent), []);
  assert.deepEqual(styleLines(itemComponent), [
    "style={{ '--progress-width': `${clampedProgress}%` } as React.CSSProperties}"
  ]);
  assert.deepEqual(styleLines(stripComponent), ['style={segmentStyle}']);
  assert.match(stripComponent, /const segmentStyle = \{\s*'--seg-fill': `\$\{fillPercent\}%`\s*\}/);
});

test('the compact panel floats under the line and its hit area never covers a card', () => {
  const panelRule = ruleBody(stripCss, '.condensed-strip-panel');
  for (const declaration of [
    /position:\s*absolute/,
    /top:\s*100%/,
    /left:\s*0/,
    /right:\s*0/,
    /z-index:\s*\d+/,
    /background:\s*var\(--theme-nav-bg\)/,
    /border-bottom:\s*1px solid var\(--theme-nav-border\)/
  ])
    assert.match(panelRule, declaration);
  // 22 px below plus the 2.5 px line: a 24 px target even where the navigation covers the top.
  assert.match(
    ruleBody(stripCss, '.condensed-strip-line::after'),
    /inset:\s*-0\.25rem 0 -1\.375rem 0/
  );
  assert.match(
    ruleBody(stripCss, ".condensed-strip-line[aria-expanded='true']::after"),
    /inset:\s*-0\.25rem 0 -0\.25rem 0/
  );
  // The line itself keeps its 2.5 px height.
  assert.match(ruleBody(stripCss, '.condensed-strip-seg'), /height:\s*0\.15625rem/);
  assert.match(stripCss, /\.condensed-strip-line:focus-visible\s*\{[^}]*outline:\s*2px solid/);
  // Motion floor: transform and opacity only, under 300 ms, and off for reduced motion.
  assert.match(panelRule, /animation:\s*condensedStripPanelIn 0\.22s/);
  assert.match(
    ruleBody(stripCss, '.condensed-strip-panel.is-closing'),
    /animation:\s*condensedStripPanelOut 0\.13s/
  );
  const reduced = stripCss.slice(stripCss.indexOf('@media (prefers-reduced-motion: reduce)'));
  assert.match(reduced, /\.condensed-strip-panel,/);
  for (const source of [stripCss, itemCss, animationsCss]) {
    assert.doesNotMatch(source, /transition:\s*all/);
    assert.doesNotMatch(source, /scale\(0\)/);
  }
  assert.doesNotMatch(barComponent + itemComponent, /transition-all/);
});

test('an invisible gap under the line keeps its target off the first card and, on touch, off the tabs', () => {
  // The first full card's buttons sit 1rem under the strip. The mouse target reaches 1.375rem
  // down and the touch target 2.25rem, so the gap is the difference. [119]
  assert.match(ruleBody(stripCss, '.condensed-strip'), /margin-bottom:\s*0\.375rem/);
  const coarse = stripCss.slice(stripCss.indexOf('@media (pointer: coarse)'));
  assert.match(ruleBody(coarse, '.condensed-strip'), /margin-bottom:\s*1\.25rem/);
  // Touch no longer reaches up over the navigation tabs, open or closed.
  assert.match(ruleBody(coarse, '.condensed-strip-line::after'), /inset:\s*0 0 -2\.25rem 0/);
  assert.match(
    ruleBody(coarse, ".condensed-strip-line[aria-expanded='true']::after"),
    /inset:\s*0 0 -0\.375rem 0/
  );
});

test('the gap under a line that is all the bar holds is empty page space, not bar', () => {
  // The bar paints its background and bottom border around everything inside it, so a margin on
  // the line's own box would paint as a band under the line. When nothing follows the line, the
  // gap moves outside the bar's box, where nothing draws. [119]
  const onlyChild = 'div:has(> .condensed-strip:last-child)';
  assert.match(ruleBody(stripCss, '.condensed-strip:last-child'), /margin-bottom:\s*0;/);
  assert.match(ruleBody(stripCss, onlyChild), /margin-bottom:\s*0\.375rem/);
  const coarse = stripCss.slice(stripCss.indexOf('@media (pointer: coarse)'));
  assert.match(ruleBody(coarse, onlyChild), /margin-bottom:\s*1\.25rem/);
  for (const body of [
    ruleBody(stripCss, '.condensed-strip'),
    ruleBody(stripCss, '.condensed-strip:last-child'),
    ruleBody(stripCss, onlyChild),
    ruleBody(coarse, '.condensed-strip'),
    ruleBody(coarse, onlyChild)
  ]) {
    assert.doesNotMatch(body, /background|border|box-shadow|outline/);
  }
});

const assertOpen = (runner) => {
  assert.equal(button(runner.tree).props['aria-expanded'], true);
  assert.ok(panel(runner.tree));
  assert.doesNotMatch(panel(runner.tree).props.className, /is-closing/);
  assert.ok(!panel(runner.tree).props.inert);
};
const keyboardOpen = (runner, key = 'Enter') => {
  runner.focus(runner.trigger);
  runner.key(key);
  runner.toggle(0);
};

test('a scheduled prefill row keeps its classification, keys, siblings and disclosure through progress', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const modules = await loadRunModules();
  const entry = modules.NOTIFICATION_REGISTRY.find((item) => item.type === 'scheduled_prefill');
  const unrelated = notice('unrelated');
  const mapping = {
    ...notice('mapping'),
    type: 'riot_game_mapping',
    controlOnly: true,
    details: { operationId: 'mapping' }
  };
  // The mapping row keeps its own Compact style in every case, so its strip segment stays put
  // while the prefill row's style varies.
  const modes = [
    ['absent', { riotMapping: 'condensed' }],
    ['condensed', { riotMapping: 'condensed', 'scheduledPrefill:Steam': 'condensed' }],
    ['full', { riotMapping: 'condensed', 'scheduledPrefill:Steam': 'full' }]
  ];

  for (const [mode, displayModes] of modes) {
    const operationId = `prefill-operation-${mode}`;
    let state = pushRun(
      modules,
      modules.createRunStoreState(),
      operationRunRow(operationId, { ...prefillRunFields, visibility: 'background' })
    );
    let notifications = [unrelated, mapping];
    const bar = makeBar(
      notifications,
      displayModes,
      modules.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY
    );
    let tree = bar.render();
    const draw = () => {
      notifications = [unrelated, mapping, ...modules.deriveNotifications(state, [])];
      tree = bar.setNotifications(notifications);
    };
    draw();
    const onProgress = modules.buildProgressHandler(entry, entry.progress, (id, build, source) => {
      state = modules.applyDetail(state, id, build, source, { requestSeq: 0 });
      draw();
    });
    const event = {
      operationId,
      serviceId: 'Steam',
      stage: 'running',
      message: 'Downloading',
      percentComplete: 10
    };
    const summarize = () => {
      const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
      const stripNodes = new Set(elements(strip));
      const groups = elements(tree).filter((node) => node.type === 'BackgroundTaskControls');
      const idsIn = (root) =>
        elements(root)
          .filter((node) => node.type === 'UnifiedNotificationItem')
          .map((node) => node.props.notification.id);
      const compactIds = groups.filter((group) => stripNodes.has(group)).flatMap(idsIn);
      const fullControlIds = groups.filter((group) => !stripNodes.has(group)).flatMap(idsIn);
      const scheduled = notifications.find((card) => card.details?.operationId === operationId);
      const stripIds = idsIn(strip);
      const branch = compactIds.includes(scheduled.id)
        ? 'compact-control'
        : fullControlIds.includes(scheduled.id)
          ? 'full-control'
          : stripIds.includes(scheduled.id)
            ? 'condensed-card'
            : 'full-card';
      return {
        branch,
        scheduled,
        segments: strip.props.segments,
        segmentKeys: strip.props.segments.map((item) => item.key)
      };
    };

    onProgress(event);
    const snapshots = [summarize()];
    const strip = makeStrip();
    strip.props.segments = snapshots[0].segments;
    strip.start();
    strip.toggle();
    assertOpen(strip);

    for (const update of [
      { message: 'Scanning', percentComplete: 25 },
      { message: 'Downloading', percentComplete: 50 },
      { message: 'Finishing', percentComplete: 75 }
    ]) {
      onProgress({ ...event, ...update });
      const snapshot = summarize();
      snapshots.push(snapshot);
      strip.segments(snapshot.segments);
      assertOpen(strip);
      assert.ok(
        elements(strip.tree)
          .filter((node) => node.type === 'StripSegment')
          .every((node) => !node.props.leaving),
        `${mode} updates retain every strip segment`
      );
    }

    const expectedBranch = mode === 'condensed' ? 'compact-control' : 'full-control';
    assert.deepEqual(
      snapshots.map((snapshot) => snapshot.branch),
      [expectedBranch, expectedBranch, expectedBranch, expectedBranch],
      `${mode} keeps the scheduled card in one render branch`
    );
    assert.deepEqual(
      snapshots.map((snapshot) => snapshot.segmentKeys),
      snapshots.map(() => ['background-controls']),
      `${mode} keeps the singleton mapping segment key`
    );
    const first = snapshots[0].scheduled;
    for (const snapshot of snapshots) {
      assert.equal(snapshot.scheduled.id, first.id);
      assert.equal(snapshot.scheduled.startedAt, first.startedAt);
      assert.equal(snapshot.scheduled.controlOnly, true);
      assert.equal(
        notifications.find((card) => card.id === unrelated.id),
        unrelated
      );
      assert.equal(
        notifications.find((card) => card.id === mapping.id),
        mapping
      );
    }

    strip.dispose();
    bar.dispose();
  }
});

test('native keyboard activation stays open while time passes and the pointer is elsewhere', () => {
  for (const key of ['Enter', ' ']) {
    const runner = makeStrip(true);
    try {
      runner.start();
      keyboardOpen(runner, key);
      runner.advance(1000);
      runner.move(runner.outside);
      assertOpen(runner);
      assert.equal(runner.document.activeElement, runner.trigger);
      assert.deepEqual(runner.trigger.focusCalls, []);
    } finally {
      runner.dispose();
    }
  }
});

test('focused keyboard sessions survive wrapper and document pointer departure', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    keyboardOpen(runner);
    runner.focus(runner.action);
    runner.leave();
    assertOpen(runner);
    runner.leave(true);
    runner.advance(1000);
    assertOpen(runner);
    // A keyboard-opened panel stays open while focus is inside, wherever the pointer goes.
    runner.move(runner.outside);
    assertOpen(runner);
    runner.focus(runner.trigger);
    assertOpen(runner);
  } finally {
    runner.dispose();
  }
});

test('keyboard focus departure closes immediately even while hovered', () => {
  for (const destination of ['outside', 'null']) {
    const runner = makeStrip(true);
    try {
      runner.start();
      runner.enter();
      keyboardOpen(runner);
      runner.key('Tab');
      runner.focus(destination === 'outside' ? runner.outside : null);
      assert.equal(button(runner.tree).props['aria-expanded'], false);
      assert.equal(panel(runner.tree)?.props.inert, true);
    } finally {
      runner.dispose();
    }
  }
});

test('desktop outside pointer dismisses without moving focus', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.enter();
    keyboardOpen(runner);
    runner.pointer();
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    assert.deepEqual(runner.trigger.focusCalls, []);
  } finally {
    runner.dispose();
  }
});

test('Escape returns contained focus before closing and leaves outside focus alone', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    keyboardOpen(runner);
    runner.focus(runner.action);
    runner.key('Escape');
    assert.equal(runner.document.activeElement, runner.trigger);
    assert.deepEqual(runner.trigger.focusCalls, [{ preventScroll: true }]);
    assert.equal(panel(runner.tree)?.props.inert, true);
    runner.toggle(0);
    runner.document.activeElement = runner.outside;
    runner.key('Escape');
    assert.equal(runner.document.activeElement, runner.outside);
    assert.equal(runner.trigger.focusCalls.length, 1);
  } finally {
    runner.dispose();
  }
});

test('Tab takes over hover disclosure but pointer presses restore pointer ownership', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.enter();
    runner.advance(135);
    assertOpen(runner);
    runner.key('Tab', runner.outside);
    runner.focus(runner.action);
    runner.leave();
    runner.advance(1000);
    assertOpen(runner);
    runner.pointer(runner.action);
    runner.leave();
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    runner.enter();
    runner.advance(135);
    runner.key('Tab', runner.outside);
    runner.pointer(runner.trigger);
    runner.focus(runner.trigger);
    runner.leave();
    assert.equal(button(runner.tree).props['aria-expanded'], false);
  } finally {
    runner.dispose();
  }
});

test('pointer delay and leave cancellation, and the next move outside closes a rested panel', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.enter();
    runner.advance(134);
    assert.equal(panel(runner.tree), undefined);
    runner.leave();
    runner.advance(500);
    assert.equal(panel(runner.tree), undefined);
    runner.enter();
    runner.advance(135);
    assertOpen(runner);
    runner.move(runner.action);
    assertOpen(runner);
    // A card leaving shrank the panel out from under the parked pointer, so no mouseleave
    // arrived; the next move lands outside and closes it.
    runner.move(runner.outside);
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    // Nothing reopens it without a fresh rest on the line.
    runner.advance(1000);
    assert.equal(button(runner.tree).props['aria-expanded'], false);
  } finally {
    runner.dispose();
  }
});

test('resting on the line opens the panel, a click then keeps it open, and the next click closes it', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.enter();
    runner.advance(135);
    assertOpen(runner);
    runner.toggle(1);
    assertOpen(runner);
    runner.advance(1000);
    assertOpen(runner);
    runner.toggle(1);
    assert.equal(button(runner.tree).props['aria-expanded'], false);
  } finally {
    runner.dispose();
  }
});

test('a panel opened by a click closes when the pointer leaves the line and the panel', () => {
  for (const departure of ['mouseleave', 'move outside']) {
    const runner = makeStrip(true);
    try {
      runner.start();
      runner.enter(false);
      runner.toggle(1);
      assertOpen(runner);
      runner.move(runner.action);
      assertOpen(runner);
      if (departure === 'mouseleave') runner.leave();
      else runner.move(runner.outside);
      assert.equal(button(runner.tree).props['aria-expanded'], false, departure);
    } finally {
      runner.dispose();
    }
  }
});

test('a hover-opened panel closes on a press outside and on Escape', () => {
  for (const dismissal of ['outside', 'Escape']) {
    const runner = makeStrip(true);
    try {
      runner.start();
      runner.enter();
      runner.advance(135);
      assertOpen(runner);
      if (dismissal === 'outside') runner.pointer();
      else runner.key('Escape');
      assert.equal(button(runner.tree).props['aria-expanded'], false, dismissal);
    } finally {
      runner.dispose();
    }
  }
});

test('layout entering under a parked pointer cannot open the compact panel', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.enter(false);
    runner.advance(500);
    assert.equal(panel(runner.tree), undefined);
    runner.leave();
    runner.enter();
    runner.advance(135);
    assertOpen(runner);
  } finally {
    runner.dispose();
  }
});

test('movement inside the strip opens after entry without restarting the delay', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.enter(false);
    runner.event(() => runner.document.dispatch('pointermove', { clientX: 2, clientY: 3 }));
    runner.advance(100);
    runner.event(() => runner.document.dispatch('pointermove', { clientX: 3, clientY: 3 }));
    runner.advance(34);
    assert.equal(panel(runner.tree), undefined);
    runner.advance(1);
    assertOpen(runner);
  } finally {
    runner.dispose();
  }
});

test('recent movement outside the strip cannot authorize a layout-only entry', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    runner.event(() => runner.document.dispatch('pointermove', { clientX: 2, clientY: 3 }));
    runner.enter(false);
    runner.advance(500);
    assert.equal(panel(runner.tree), undefined);
  } finally {
    runner.dispose();
  }
});

test('visibility loss closes keyboard sessions while visible changes preserve them', () => {
  const runner = makeStrip(true);
  try {
    runner.start();
    keyboardOpen(runner);
    runner.event(() => runner.document.dispatch('visibilitychange', {}));
    assertOpen(runner);
    runner.event(() => runner.window.dispatch('blur', {}));
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    runner.toggle(0);
    runner.event(() => {
      runner.document.visibilityState = 'hidden';
      runner.document.dispatch('visibilitychange', {});
    });
    assert.equal(button(runner.tree).props['aria-expanded'], false);
  } finally {
    runner.dispose();
  }
});

test('pending hover cannot reopen after explicit dismissal or segment loss', () => {
  for (const reason of ['toggle', 'Escape', 'outside', 'segments', 'blur', 'hidden']) {
    const runner = makeStrip(true);
    try {
      runner.start();
      runner.enter();
      runner.advance(30);
      if (reason === 'segments') runner.segments([]);
      else if (reason === 'blur') runner.event(() => runner.window.dispatch('blur', {}));
      else if (reason === 'hidden')
        runner.event(() => {
          runner.document.visibilityState = 'hidden';
          runner.document.dispatch('visibilitychange', {});
        });
      else {
        runner.toggle();
        if (reason === 'toggle') runner.toggle();
        else if (reason === 'Escape') runner.key('Escape');
        else runner.pointer();
      }
      runner.advance(200);
      if (button(runner.tree))
        assert.equal(button(runner.tree).props['aria-expanded'], false, reason);
      if (reason === 'segments') {
        runner.toggle();
        runner.segments([segment()]);
        assert.equal(panel(runner.tree), undefined);
      }
    } finally {
      runner.dispose();
    }
  }
});

test('tap and keyboard sessions preserve non-hover dismissal and cancellation boundaries', () => {
  const runner = makeStrip();
  try {
    runner.start();
    runner.toggle();
    runner.leave();
    runner.advance(1000);
    assertOpen(runner);
    runner.pointer();
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    keyboardOpen(runner);
    runner.focus(runner.action);
    runner.focus(runner.outside);
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    keyboardOpen(runner);
    runner.key('Escape');
    runner.toggle(0);
    assertOpen(runner);
    runner.reduced(true);
    runner.key('Escape');
    assert.equal(panel(runner.tree), undefined);
    runner.reduced(false);
    keyboardOpen(runner);
    runner.segments([]);
    runner.advance(30);
    runner.toggle(0);
    runner.segments([segment()]);
    assert.equal(panel(runner.tree), undefined);
    runner.toggle(1);
    assertOpen(runner);
  } finally {
    runner.dispose();
  }
});

test('unmount clears hover, exit, and all document boundary callbacks', () => {
  for (const state of ['pending', 'open', 'closing']) {
    const runner = makeStrip(true);
    runner.start();
    runner.enter();
    if (state !== 'pending') runner.advance(135);
    if (state === 'closing') runner.leave();
    runner.flushPassive();
    // An open panel runs no timer at all: nothing re-polls the pointer while it is open.
    assert.equal(runner.timers.size > 0, state !== 'open', state);
    runner.dispose();
    assert.equal(runner.timers.size, 0);
    for (const target of [runner.window, runner.document, runner.document.documentElement]) {
      assert.ok([...target.listeners.values()].every((listeners) => listeners.size === 0));
    }
  }
});

// ── A tooltip dismissed with Escape stays dismissed [99] ────────────────────

test('an Escape keeps an open tooltip hidden when the pointer reached its control just before', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const TOOLTIP = 'src/components/ui/Tooltip.tsx';
  const shows = [];
  const setShow = (value) => shows.push(value);
  const showTimeoutRef = { current: null };
  // The same function as the page's open slot and this box's own close: the box is open.
  const closeNow = () => undefined;
  const handleMouseEnter = bindLifted(liftConstArrow(TOOLTIP, 'handleMouseEnter'), {
    tooltipsDisabled: false,
    hideTimeoutRef: { current: null },
    showTimeoutRef,
    addsNothing: () => false,
    closeOpenTooltip: closeNow,
    closeNow,
    setX: () => undefined,
    setY: () => undefined,
    setShow,
    SHOW_DELAY_MS: 150
  });
  const handleKeyDown = bindLifted(liftConstArrow(TOOLTIP, 'handleKeyDown'), {
    showTimeoutRef,
    setShow
  });
  let stopped = 0;

  handleMouseEnter({ clientX: 0, clientY: 0 });
  handleKeyDown({
    key: 'Escape',
    stopPropagation: () => {
      stopped += 1;
    }
  });
  t.mock.timers.tick(150);

  assert.deepEqual(shows, [false], 'the box never reopens after the Escape');
  // The first Escape stops here, so only the second one reaches the panel.
  assert.equal(stopped, 1);
});

// ── One card layout for every run type (criteria 14, 43, 50) ────────────────

const itemSource = parseSource(
  'src/components/common/UnifiedNotificationItem.tsx',
  ts.ScriptKind.TSX
);
const { NOTIFICATION_TITLE_KEYS } = await import(
  await compileToUrl('../src/contexts/notifications/notificationTitleKeys.ts')
);
const ICONS = ['CheckCircle', 'AlertCircle', 'X', 'XCircle', 'Info', 'Clock', 'MinusCircle'];
const STATUS_ICONS = new Set([...ICONS.filter((name) => name !== 'X'), 'LoadingSpinner']);
const iconBindings = Object.fromEntries(ICONS.map((name) => [name, name]));
const spinnerSizes = bindLifted(
  `() => (${initializer(parseSource('src/components/common/LoadingSpinner.tsx', ts.ScriptKind.TSX), 'sizeClasses')})`,
  {}
)();
const liftItemPart = (name, bindings) =>
  bindLifted(initializer(itemSource, name), { React: h, ...bindings }, jsx);
const renderItem = (notification, isAnimatingOut = false, connectionLost = false, onCancel) =>
  bindLifted(
    findSoleNode(
      itemSource,
      'UnifiedNotificationItem function',
      (node) => ts.isFunctionExpression(node) && node.name?.text === 'UnifiedNotificationItem'
    ).getText(itemSource),
    {
      React: h,
      useTranslation: () => ({
        t: (key, values) => (values ? `${key}:${JSON.stringify(values)}` : key)
      }),
      useSteamWebApiStatus: () => ({ status: { hasApiKey: true } }),
      formatBytes: (bytes) => `${bytes} B`,
      Tooltip: 'Tooltip',
      Badge: 'Badge',
      Button: 'Button',
      LoadingSpinner: 'LoadingSpinner',
      ...iconBindings,
      isTerminalNotificationStatus,
      NOTIFICATION_TITLE_KEYS,
      CANCEL_CONFIG_BY_TYPE: {},
      getNotificationVariant,
      getNotificationIcon: liftItemPart('getNotificationIcon', {
        ...iconBindings,
        LoadingSpinner: 'LoadingSpinner'
      }),
      renderCompletionDetails: liftItemPart('renderCompletionDetails', { formatCount: String }),
      renderProgressBar: liftItemPart('renderProgressBar', {}),
      useNotificationAnnouncement: () => '',
      FORCE_KILL_TOOLTIP_KEY: 'common.notifications.forceKillOperation'
    },
    jsx
  )({ notification, onDismiss: () => undefined, onCancel, isAnimatingOut, connectionLost });

const textOf = (node) =>
  typeof node === 'string' || typeof node === 'number'
    ? String(node)
    : node && typeof node === 'object'
      ? node.props.children.map(textOf).join('')
      : '';
/** The card body's slots in order, named by what each one holds. */
const slotsOf = (tree) => {
  const column = elements(tree).find((node) => node.props?.className === 'flex-1 min-w-0');
  const body = column.props.children.filter((child) => child && typeof child === 'object').at(-1);
  return body.props.children
    .filter((child) => child && typeof child === 'object')
    .map((slot) => {
      const className = slot.props.className ?? '';
      const name = className.includes('text-sm font-medium')
        ? 'message'
        : elements(slot).some((node) => node.props?.role === 'progressbar')
          ? 'progress'
          : slot.type === 'p'
            ? 'reconnecting'
            : className.includes('whitespace-normal')
              ? 'detail'
              : 'error';
      return { name, slot };
    });
};

test('every run type draws the same slots in the same places', () => {
  const cases = {
    game_removal: { gameName: 'Game', filesDeleted: 3, bytesFreed: 1024, logEntriesRemoved: 2 },
    depot_mapping: { isLoggedOn: true },
    service_removal: { service: 'steam', filesDeleted: 3, bytesFreed: 1024 },
    cache_clearing: { filesDeleted: 3, bytesDeleted: 1024 },
    corruption_detection: {},
    scheduled_prefill: { service: 'Steam' }
  };
  assert.doesNotMatch(itemComponent, /Trash2|sm:truncate/);
  for (const [type, details] of Object.entries(cases)) {
    for (const status of ['running', 'completed']) {
      const label = `${type} ${status}`;
      const tree = renderItem({
        id: type,
        type,
        status,
        message: `${type} message`,
        detailMessage: `${type} detail`,
        progress: status === 'running' ? 40 : 100,
        startedAt: new Date(0),
        details: { operationId: type, ...details }
      });
      assert.match(
        tree.props.className,
        new RegExp(
          `^notification-card notification-status--${status === 'running' ? 'info' : 'success'} `
        ),
        label
      );
      assert.equal(tree.props.style, undefined, `${label}: the root sets no inline style`);
      const slots = slotsOf(tree);
      assert.deepEqual(
        slots.map((item) => item.name),
        status === 'running' ? ['message', 'detail', 'progress'] : ['message', 'detail'],
        label
      );
      assert.equal(textOf(slots[0].slot), `${type} message`, label);
      const detail = slots[1].slot;
      assert.equal(textOf(detail.props.children[0]), `${type} detail`, `${label}: detail first`);
      const inDetail = new Set(elements(detail));
      const badges = elements(tree).filter((node) => node.type === 'Badge');
      assert.equal(badges.length, type === 'depot_mapping' ? 2 : 0, label);
      assert.ok(
        badges.every((badge) => inDetail.has(badge)),
        `${label}: badges sit in the detail slot`
      );
      const icons = elements(tree).filter((node) => STATUS_ICONS.has(node.type));
      assert.deepEqual(
        icons.map((icon) => icon.type),
        [status === 'running' ? 'LoadingSpinner' : 'CheckCircle'],
        `${label}: one status icon`
      );
      for (const icon of icons) {
        assert.match(icon.props.className, /\bnotification-card__icon\b/);
        assert.equal(icon.props.style, undefined);
      }
      if (status === 'running') {
        const track = elements(tree).find(
          (node) => node.props?.className === 'notification-progress-track'
        );
        assert.equal(track.props.style, undefined, `${label}: the track sets no style`);
        const fill = elements(tree).find(
          (node) => node.props?.className === 'notification-progress-fill'
        );
        assert.deepEqual(fill.props.style, { '--progress-width': '40%' }, label);
      }
    }
  }
});

test('every status holds the same 16 px icon slot, so the text starts at the same x', () => {
  const statuses = [
    'pending',
    'running',
    'cancelling',
    'waiting',
    'completed',
    'failed',
    'cancelled',
    'skipped'
  ];
  const layouts = statuses.map((status) => {
    const tree = renderItem({
      ...notice(status),
      type: 'game_removal',
      status,
      progress: 40,
      details: { operationId: status }
    });
    const [icon, liveRegion, column] = tree.props.children;
    assert.ok(STATUS_ICONS.has(icon?.type), `${status}: the first slot is a status icon`);
    assert.match(icon.props.className, /\bnotification-card__icon\b/, status);
    assert.match(icon.props.className, /\bflex-shrink-0\b/, status);
    // A lucide icon carries its size in its class; the spinner takes it from its size prop.
    const size =
      icon.type === 'LoadingSpinner'
        ? spinnerSizes[icon.props.size]
        : icon.props.className.match(/\bw-\d+ h-\d+\b/)?.[0];
    return [size, liveRegion.props.className, column.props.className];
  });
  for (const [index, layout] of layouts.entries())
    assert.deepEqual(
      layout,
      ['w-4 h-4', 'sr-only select-none', 'flex-1 min-w-0'],
      `${statuses[index]}: icon, live region, text column`
    );
  assert.match(itemComponent, /motion-reduce:animate-none/, 'the spinner stops for reduced motion');
});

test('a card leaving fades through the Tailwind opacity utility', () => {
  const card = { ...notice('leaving'), status: 'completed', details: {} };
  assert.doesNotMatch(renderItem(card).props.className, /\bopacity-0\b/);
  const leaving = renderItem(card, true);
  assert.match(leaving.props.className, /\bopacity-0\b/);
  assert.match(
    leaving.props.className,
    /transition-opacity duration-300 ease-out motion-reduce:transition-none/
  );
});

test('a kept schedule card shows its whole failure count and latest success, wrapped', () => {
  const detailMessage = 'Failed 3 times in a row. The latest run succeeded.';
  const tree = renderItem({
    id: 'kept',
    type: 'game_detection',
    status: 'failed',
    message: 'Game detection failed',
    detailMessage,
    error: 'Disk unavailable',
    startedAt: new Date(0),
    details: { operationId: 'kept', closeOperationIds: ['kept'] }
  });
  const slots = slotsOf(tree);
  assert.deepEqual(
    slots.map((item) => item.name),
    ['message', 'detail', 'error']
  );
  const detail = slots[1].slot;
  assert.equal(textOf(detail), detailMessage);
  assert.match(detail.props.className, /\bwhitespace-normal\b/);
  assert.match(detail.props.className, /\bbreak-words\b/);
  assert.doesNotMatch(detail.props.className, /truncate/);
  assert.equal(
    elements(tree).filter((node) => STATUS_ICONS.has(node.type)).length,
    1,
    'no extra icon beside the status icon'
  );
  assert.match(tree.props.className, /\bnotification-status--error\b/);
});

test('a run that succeeded with a warning is an amber card with its warning line', () => {
  const tree = renderItem({
    id: 'scan',
    type: 'eviction_scan',
    status: 'completed',
    message: 'Eviction scan complete',
    detailMessage: 'Game detection failed: disk unavailable',
    startedAt: new Date(0),
    details: { operationId: 'scan', notificationType: 'warning' }
  });
  assert.match(tree.props.className, /\bnotification-status--warning\b/);
  const slots = slotsOf(tree);
  assert.equal(
    textOf(slots.find((item) => item.name === 'detail').slot),
    'Game detection failed: disk unavailable'
  );
  assert.deepEqual(
    elements(tree)
      .filter((node) => STATUS_ICONS.has(node.type))
      .map((node) => node.type),
    ['AlertCircle']
  );
});

test('a background row that waits says what it waits for, in the sentence case it was written in', () => {
  const row = (status, message) => {
    const tree = renderItem({
      ...notice('row'),
      controlOnly: true,
      status,
      message,
      details: { operationId: 'row' }
    });
    return elements(tree).find((node) => node.props?.role === 'status');
  };

  const waiting = row('waiting', 'Waiting for Cache File Scan to finish...');
  assert.equal(textOf(waiting), 'Waiting for Cache File Scan to finish...');
  assert.doesNotMatch(waiting.props.className, /\bcapitalize\b/);

  const running = row('running', 'Detecting games');
  assert.equal(textOf(running), 'common.notifications.condensedStatus.running');
  assert.match(running.props.className, /\bcapitalize\b/);
});

// ── While the server is unreachable [171] [174] ─────────────────────────────

test('a running run card offers its close button only while the connection is lost', () => {
  const closeButtons = (tree) =>
    elements(tree).filter(
      (node) => node.type === 'button' && node.props['aria-label'] === 'common.dismiss'
    );
  const run = {
    ...notice('run'),
    type: 'eviction_scan',
    details: { operationId: 'op-1', operationIds: ['op-1'] }
  };
  // A card the browser drew itself carries no run ids, so it has nothing to hide.
  const own = { ...notice('own'), details: {} };
  for (const connectionLost of [true, false]) {
    assert.equal(
      closeButtons(renderItem(run, false, connectionLost)).length,
      connectionLost ? 1 : 0,
      `run card, lost=${connectionLost}`
    );
    assert.equal(closeButtons(renderItem(own, false, connectionLost)).length, 0, `own card`);
  }
  // One X per card: the cancel X yields to the close button.
  assert.match(itemComponent, /\{!closable &&\s+notification\.type in CANCEL_CONFIG_BY_TYPE/);
});

test('a background row hides its Cancel while the connection is lost and gains no close button', () => {
  const background = {
    ...notice('row'),
    controlOnly: true,
    details: { operationId: 'row', operationIds: ['row'] }
  };
  for (const connectionLost of [true, false]) {
    const tree = renderItem(background, false, connectionLost, () => undefined);
    const controls = elements(tree).filter(
      (node) => node.type === 'Button' || node.type === 'button'
    );
    assert.deepEqual(
      controls.map((node) => node.props.className),
      connectionLost ? [] : ['background-task-control-row__cancel'],
      `lost=${connectionLost}`
    );
  }
});

test('a long failure reason wraps inside the card', () => {
  const reason = `/data/cache/${'steam/depot/'.repeat(8)}chunk-000001`;
  assert.equal(reason.length, 120);
  const tree = renderItem({
    ...notice('failed'),
    status: 'failed',
    message: 'Failed to sign in to Steam',
    error: reason,
    details: { operationId: 'failed' }
  });
  const error = slotsOf(tree).find((item) => item.name === 'error').slot;
  assert.equal(textOf(error), reason);
  assert.match(error.props.className, /\bbreak-words\b/);
});

test('a prefill card leaves the reconnecting line to the connection banner while it is up', () => {
  const card = {
    ...notice('prefill'),
    type: 'scheduled_prefill',
    details: { operationId: 'prefill', operationIds: ['prefill'], connectionRecovering: true }
  };
  for (const connectionLost of [true, false])
    assert.equal(
      slotsOf(renderItem(card, false, connectionLost)).some((item) => item.name === 'reconnecting'),
      !connectionLost,
      `lost=${connectionLost}`
    );
});

test('a prefill card leaves the reconnecting announcement to the connection banner while it is up', () => {
  const announce = bindLifted(
    findSoleNode(
      itemSource,
      'announcement hook',
      (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'useNotificationAnnouncement'
    ).getText(itemSource),
    {
      useTranslation: () => ({ t: (key) => key }),
      useState: (initial) => [initial, () => undefined],
      useRef: (initial) => ({ current: initial }),
      useEffect: () => undefined,
      ANNOUNCEMENT_MIN_INTERVAL_MS: 5000,
      isTerminalNotificationStatus
    }
  );
  const card = {
    ...notice('prefill'),
    type: 'scheduled_prefill',
    details: { connectionRecovering: true }
  };
  for (const connectionLost of [true, false])
    assert.equal(
      announce(card, connectionLost).includes('prefill.progress.reconnecting'),
      !connectionLost,
      `lost=${connectionLost}`
    );
});
