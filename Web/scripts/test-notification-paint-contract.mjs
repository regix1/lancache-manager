import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  findSoleNode,
  loadNotificationModules,
  MemoryStorage,
  notificationEvents,
  parseSource
} from './transpile-module.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = resolve(__dirname, '..');
const stripCss = readFileSync(
  resolve(WEB_ROOT, 'src/components/common/CondensedNotificationStrip.css'),
  'utf8'
);
const barComponent = readFileSync(
  resolve(WEB_ROOT, 'src/components/common/UniversalNotificationBar.tsx'),
  'utf8'
);
const itemComponent = readFileSync(
  resolve(WEB_ROOT, 'src/components/common/UnifiedNotificationItem.tsx'),
  'utf8'
);

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
  color: 'var(--theme-success)'
});
const makeBar = (
  initial = [],
  displayModes = {},
  typeToServiceKey = {},
  entityTypes = new Set()
) => {
  const context = {
    notifications: initial,
    removeNotification: () => assert.fail('rendering must not remove notifications'),
    updateNotification: () => assert.fail('rendering must not update notifications')
  };
  const runner = mount(barSource, 'UniversalNotificationBar', {
    useNotifications: () => context,
    themeService: { getDisableStickyNotificationsSync: () => false },
    useScheduleDisplayModes: () => displayModes,
    useMediaQuery: () => false,
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
    NOTIFICATION_IDS: {},
    TYPES_WITH_A_CARD_PER_ENTITY: entityTypes,
    isTerminalNotificationStatus: (status) =>
      ['completed', 'failed', 'cancelled', 'skipped'].includes(status),
    platformDisplayModeKey: (service, platform) => `${service}:${platform}`,
    getNotificationColor: () => 'var(--theme-success)',
    handleCancel: () => {
      assert.fail('rendering must not cancel work');
    },
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

test('silent mapping refreshes stay inside the compact strip through every event', async () => {
  globalThis.localStorage = new MemoryStorage();
  globalThis.sessionStorage = new MemoryStorage();
  const modules = await loadNotificationModules();
  const full = { ...notice('prefill'), type: 'scheduled_prefill' };
  const compact = notice('detection');
  let notifications = [full, compact];
  const bar = makeBar(
    notifications,
    { gameDetection: 'condensed' },
    modules.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY
  );
  const events = notificationEvents().current;
  let commits = 0;
  const setNotifications = (update) => {
    notifications = typeof update === 'function' ? update(notifications) : update;
    const tree = bar.setNotifications(notifications);
    const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
    const controls = elements(tree).filter((node) => node.type === 'BackgroundTaskControls');
    assert.deepEqual(
      controls,
      elements(strip).filter((node) => node.type === 'BackgroundTaskControls'),
      'a refresh must not insert a separate background row above the full cards'
    );
    assert.equal(
      notifications.find((card) => card.id === full.id),
      full
    );
    assert.equal(
      notifications.find((card) => card.id === compact.id),
      compact
    );
    commits += 1;
  };

  for (const type of ['riot_game_mapping', 'battle_net_game_mapping']) {
    const entry = modules.NOTIFICATION_REGISTRY.find((item) => item.type === type);
    const fields = {
      operationId: `${type}-refresh`,
      showNotification: false,
      context: { processed: 1, total: 1, mapped: 0 }
    };
    modules.buildStartedHandler(
      entry,
      entry.started,
      setNotifications,
      () => undefined,
      events
    )(fields);
    modules.buildProgressHandler(
      entry,
      entry.progress,
      setNotifications,
      () => undefined,
      () => undefined,
      events
    )({ ...fields, status: 'running', percentComplete: 90 });
    assert.equal(notifications.find((card) => card.type === type)?.controlOnly, true);
    modules.buildCompleteHandler(
      entry,
      setNotifications,
      () => undefined,
      events
    )({
      ...fields,
      success: true,
      status: type === 'battle_net_game_mapping' ? 'skipped' : 'completed'
    });
    assert.equal(
      notifications.some((card) => card.type === type),
      false
    );
  }
  assert.equal(commits, 6);
  bar.dispose();
});

test('background controls default to compact while explicit full settings remain full', () => {
  for (const status of ['waiting', 'running', 'cancelling']) {
    for (const mode of [undefined, 'condensed', 'full']) {
      const card = { ...notice(), controlOnly: true, status };
      const bar = makeBar([card], mode ? { gameDetection: mode } : {}, {
        game_detection: 'gameDetection'
      });
      const tree = bar.render();
      const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
      assert.equal(
        elements(strip).some((node) => node.type === 'BackgroundTaskControls'),
        mode !== 'full',
        `${status} controls with ${mode ?? 'no'} display preference`
      );
      assert.equal(
        elements(tree).filter((node) => node.type === 'BackgroundTaskControls').length,
        1
      );
      bar.dispose();
    }
  }
});

test('normal notifications and terminal failures retain the full-view default', () => {
  for (const card of [notice(), { ...notice(), controlOnly: true, status: 'failed' }]) {
    const bar = makeBar([card]);
    const tree = bar.render();
    const strip = elements(tree).find((node) => node.type === 'CondensedNotificationStrip');
    assert.equal(
      elements(strip).some((node) => node.type === 'UnifiedNotificationItem'),
      false
    );
    assert.equal(
      elements(tree).some((node) => node.type === 'UnifiedNotificationItem'),
      true
    );
    bar.dispose();
  }
});

test('per-platform background tasks retain their full-view default', async () => {
  const modules = await loadNotificationModules();
  const entityTypes = new Set(
    modules.NOTIFICATION_REGISTRY.filter((entry) => entry.getId).map((entry) => entry.type)
  );
  const card = {
    ...notice('operation-prefill'),
    type: 'scheduled_prefill',
    controlOnly: true,
    details: { service: 'Steam', operationId: 'prefill' }
  };
  const bar = makeBar(
    [card],
    { 'scheduledPrefill:Steam': 'full' },
    modules.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY,
    entityTypes
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
const makeStrip = (canHover = false) => {
  const preference = { reduced: false };
  const published = [];
  const constants = Object.fromEntries(
    [
      'PANEL_EXIT_MS',
      'LINE_EXIT_MS',
      'SEG_EXIT_MS',
      'HOVER_OPEN_DELAY_MS',
      'OPEN_HOVER_RECHECK_MS',
      'GLOW_COLOR_BY_STATUS_COLOR',
      'UNMAPPED_GLOW_COLOR'
    ].map((name) => [name, bindLifted(`() => (${initializer(stripSource, name)})`, {})()])
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
    onOpenChange: (open) => published.push(open),
    children: 'cards'
  };
  return Object.assign(runner, {
    preference,
    published,
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
        if (pointerMoved) runner.document.dispatch('pointermove', { clientX: 2, clientY: 3 });
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
    runner.setNotifications([]);
    runner.advance(299);
    assert.equal(runner.tree.props.children[0].props.style.opacity, 1);
    runner.advance(1);
    assert.equal(runner.tree.props.children[0].props.style.opacity, 0);
    const tree = runner.setNotifications([notice()]);
    assert.deepEqual(
      tree.props.children[0].props.style,
      { opacity: 1, transform: 'translateY(0)' },
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

test('first compact close retains a closing inert panel and publishes its edge until expiry', () => {
  const runner = makeStrip();
  try {
    runner.start();
    assert.deepEqual(runner.published, [false]);
    assert.ok(panel(runner.toggle()));
    assert.deepEqual(runner.published, [false, true]);
    runner.flushPassive();
    const closing = panel(runner.toggle());
    assert.ok(closing, 'the first close commit must retain the panel');
    assert.match(closing.props.className, /is-closing/);
    assert.equal(closing.props.inert, true);
    assert.deepEqual(runner.published, [false, true]);
    runner.advance(149);
    assert.ok(panel(runner.tree));
    runner.advance(1);
    assert.equal(panel(runner.tree), undefined);
    assert.deepEqual(runner.published, [false, true, false]);
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
      assert.equal(runner.published.at(-1), false);
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
    assert.deepEqual(runner.published, [false, true]);
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
  const source = readFileSync(
    resolve(WEB_ROOT, 'src/components/common/UnifiedNotificationItem.tsx'),
    'utf8'
  );
  const root = source.match(/className="flex items-start sm:items-center[^"]*"/)[0];
  assert.match(root, /\brounded\s/, 'normal card root must use the existing base radius');
  assert.doesNotMatch(root, /rounded-lg/);
  assert.match(source, /background-task-control-row[^\n]*\brounded\b/);
  const background = readFileSync(
    resolve(WEB_ROOT, 'src/components/common/BackgroundTaskControls.css'),
    'utf8'
  );
  assert.match(background, /border-radius:\s*var\(--theme-border-radius\)/);
  assert.match(stripCss, /\.condensed-strip-line\s*\{[^}]*border-radius:\s*0/);
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

test('native keyboard activation stays open through four pointer rechecks', () => {
  for (const key of ['Enter', ' ']) {
    const runner = makeStrip(true);
    try {
      runner.start();
      keyboardOpen(runner, key);
      runner.advance(1000);
      assertOpen(runner);
      assert.equal(runner.document.activeElement, runner.trigger);
      assert.deepEqual(runner.trigger.focusCalls, []);
      assert.deepEqual(runner.published, [false, true]);
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

test('pointer delay, leave cancellation, and both recheck signals remain independent', () => {
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
    runner.host.hovered = false;
    runner.advance(250);
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    runner.enter();
    runner.advance(135);
    runner.event(() => runner.document.dispatch('pointermove', { clientX: 2, clientY: 3 }));
    runner.document.hit = runner.outside;
    runner.advance(250);
    assert.equal(button(runner.tree).props['aria-expanded'], false);
    runner.focus(runner.trigger);
    runner.toggle(1);
    runner.leave();
    assert.equal(button(runner.tree).props['aria-expanded'], false);
  } finally {
    runner.dispose();
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

test('unmount clears hover, recheck, exit, and all document boundary callbacks', () => {
  for (const state of ['pending', 'open', 'closing']) {
    const runner = makeStrip(true);
    runner.start();
    runner.enter();
    if (state !== 'pending') runner.advance(135);
    if (state === 'closing') runner.leave();
    runner.flushPassive();
    assert.ok(runner.timers.size > 0);
    runner.dispose();
    assert.equal(runner.timers.size, 0);
    for (const target of [runner.window, runner.document, runner.document.documentElement]) {
      assert.ok([...target.listeners.values()].every((listeners) => listeners.size === 0));
    }
  }
});
