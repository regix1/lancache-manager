import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, findSoleNode, transpile } from './transpile-module.mjs';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(scriptDirectory, '..');
const followPath =
  process.env.ANCHOR_FOLLOW_SOURCE ?? resolve(webRoot, 'src/hooks/useAnchorFollow.ts');
const panelPath =
  process.env.ANCHORED_PANEL_SOURCE ?? resolve(webRoot, 'src/hooks/useAnchoredPanel.ts');
const exitPath = resolve(webRoot, 'src/hooks/useExitPresence.ts');
const { clampToViewport } = await import(await compileToUrl('../src/utils/viewportClamp.ts'));

const parseFile = (path) =>
  ts.createSourceFile(
    path,
    readFileSync(path, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS
  );

const declarationText = (source, name) => {
  const declaration = findSoleNode(
    source,
    `${name} declaration`,
    (node) =>
      (ts.isFunctionDeclaration(node) && node.name?.text === name) ||
      (ts.isVariableDeclaration(node) && node.name.getText(source) === name)
  );
  const statement = ts.isVariableDeclaration(declaration) ? declaration.parent.parent : declaration;
  return statement.getText(source).replace(/^export\s+/, '');
};

const loadDeclarations = (path, names, bindings) => {
  const source = parseFile(path);
  const selected = names.map((name) => declarationText(source, name)).join('\n');
  const compiled = transpile(
    `${selected}\nmodule.exports = { ${names.join(', ')} };`,
    ts.ModuleKind.CommonJS
  );
  const loaded = { exports: {} };
  const bindingNames = Object.keys(bindings);
  new Function('module', 'exports', ...bindingNames, compiled)(
    loaded,
    loaded.exports,
    ...bindingNames.map((name) => bindings[name])
  );
  return loaded.exports;
};

const createTarget = () => {
  const listeners = new Map();
  return {
    listeners,
    addEventListener(name, callback) {
      if (!listeners.has(name)) listeners.set(name, new Set());
      listeners.get(name).add(callback);
    },
    removeEventListener(name, callback) {
      listeners.get(name)?.delete(callback);
    },
    dispatch(name, event = {}) {
      for (const callback of [...(listeners.get(name) ?? [])]) callback(event);
    }
  };
};

const createHookRun = () => {
  const slots = [];
  const passive = new Map();
  const layout = new Map();
  const frames = new Map();
  const timers = new Map();
  const resizeObservers = new Set();
  const intersectionObservers = new Set();
  const warnings = [];
  const observerErrors = [];
  const windowTarget = createTarget();
  const documentTarget = createTarget();
  let cursor = 0;
  let dirty = false;
  let frameSequence = 0;
  let timerSequence = 0;
  let time = 0;
  let phase = null;
  let hook = () => undefined;
  let props;
  let output;
  let mounted = true;
  let renderCount = 0;

  const same = (left, right) =>
    left !== undefined &&
    right !== undefined &&
    left.length === right.length &&
    left.every((value, index) => Object.is(value, right[index]));

  const useState = (initial) => {
    const index = cursor++;
    slots[index] ??= { value: typeof initial === 'function' ? initial() : initial };
    return [
      slots[index].value,
      (update) => {
        const next = typeof update === 'function' ? update(slots[index].value) : update;
        if (!Object.is(next, slots[index].value)) {
          slots[index].value = next;
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

  const registerEffect = (queue) => (setup, dependencies) => {
    const index = cursor++;
    const slot = (slots[index] ??= {});
    if (!same(slot.dependencies, dependencies)) {
      slot.dependencies = dependencies;
      queue.set(index, setup);
    }
  };

  const useCallback = (callback, dependencies) => {
    const index = cursor++;
    const slot = (slots[index] ??= {});
    if (!same(slot.dependencies, dependencies)) {
      slot.dependencies = dependencies;
      slot.callback = callback;
    }
    return slot.callback;
  };

  const runEffects = (queue, nextPhase) => {
    const pending = [...queue];
    queue.clear();
    for (const [index, setup] of pending) {
      slots[index].cleanup?.();
      phase = nextPhase;
      slots[index].cleanup = setup();
      phase = null;
    }
  };

  const commit = () => {
    if (!mounted) return output;
    let passes = 0;
    do {
      assert.ok(++passes < 30, 'layout updates settle');
      dirty = false;
      cursor = 0;
      phase = 'render';
      output = hook(props);
      renderCount += 1;
      phase = null;
      runEffects(layout, 'layout');
    } while (dirty && mounted);
    return output;
  };

  const flushOrdinary = () => {
    if (dirty && mounted) commit();
    return output;
  };

  const flushPassive = () => {
    let passes = 0;
    while ((passive.size > 0 || dirty) && mounted) {
      assert.ok(++passes < 30, 'passive updates settle');
      if (passive.size > 0) runEffects(passive, 'passive');
      if (dirty) commit();
    }
    return output;
  };

  const requestAnimationFrame = (callback) => {
    const id = ++frameSequence;
    frames.set(id, callback);
    return id;
  };

  const cancelAnimationFrame = (id) => frames.delete(id);

  const setTimeout = (callback, delay) => {
    const id = ++timerSequence;
    timers.set(id, { callback, due: time + delay });
    return id;
  };

  const clearTimeout = (id) => timers.delete(id);

  const flushSync = (callback) => {
    if (phase === 'render' || phase === 'layout' || phase === 'passive') {
      warnings.push(`flushSync called during ${phase}`);
    }
    callback();
    flushOrdinary();
  };

  class ResizeObserver {
    constructor(callback) {
      this.callback = callback;
      this.active = true;
      this.target = null;
      resizeObservers.add(this);
    }
    observe(target) {
      this.target = target;
    }
    disconnect() {
      this.active = false;
      this.target = null;
    }
  }

  class IntersectionObserver {
    constructor(callback) {
      this.callback = callback;
      this.active = true;
      this.target = null;
      intersectionObservers.add(this);
    }
    observe(target) {
      this.target = target;
    }
    disconnect() {
      this.active = false;
      this.target = null;
    }
  }

  const window = Object.assign(windowTarget, {
    scrollX: 0,
    scrollY: 0,
    innerWidth: 1000,
    innerHeight: 600,
    scrollTo({ left, top }) {
      this.scrollX = left;
      this.scrollY = top;
    }
  });
  const document = Object.assign(documentTarget, {
    documentElement: { clientWidth: 1000, clientHeight: 600 }
  });

  const bindings = {
    useState,
    useRef,
    useCallback,
    useEffect: registerEffect(passive),
    useLayoutEffect: registerEffect(layout),
    flushSync,
    requestAnimationFrame,
    cancelAnimationFrame,
    setTimeout,
    clearTimeout,
    ResizeObserver,
    IntersectionObserver,
    window,
    document
  };

  const dispose = () => {
    if (!mounted) return;
    mounted = false;
    for (const slot of slots) {
      slot?.cleanup?.();
      if (slot) slot.cleanup = undefined;
    }
    passive.clear();
    layout.clear();
  };

  return {
    bindings,
    warnings,
    observerErrors,
    window,
    document,
    get output() {
      return output;
    },
    get renderCount() {
      return renderCount;
    },
    setHook(next) {
      hook = next;
    },
    render(nextProps) {
      props = nextProps;
      return commit();
    },
    flushOrdinary,
    flushPassive,
    runFrame() {
      const next = frames.entries().next().value;
      assert.ok(next, 'a frame is queued');
      const [id, callback] = next;
      frames.delete(id);
      phase = 'frame';
      callback(time);
      phase = null;
      return output;
    },
    emitResize(target) {
      for (const observer of [...resizeObservers]) {
        if (!observer.active || (target !== undefined && observer.target !== target)) continue;
        phase = 'observer';
        try {
          observer.callback([{ target: observer.target }]);
        } catch (error) {
          observerErrors.push(error);
          throw error;
        } finally {
          phase = null;
        }
      }
      return output;
    },
    emitIntersection(isIntersecting) {
      for (const observer of [...intersectionObservers]) {
        if (!observer.active) continue;
        phase = 'observer';
        observer.callback([{ target: observer.target, isIntersecting }]);
        phase = null;
      }
    },
    advance(milliseconds) {
      const end = time + milliseconds;
      for (;;) {
        const next = [...timers].sort(
          (left, right) => left[1].due - right[1].due || left[0] - right[0]
        )[0];
        if (!next || next[1].due > end) break;
        time = next[1].due;
        timers.delete(next[0]);
        phase = 'timer';
        next[1].callback();
        phase = null;
        flushOrdinary();
        flushPassive();
      }
      time = end;
      return output;
    },
    pendingFrames: () => frames.size,
    pendingTimers: () => timers.size,
    windowListeners: (name) => window.listeners.get(name)?.size ?? 0,
    documentListeners: (name) => document.listeners.get(name)?.size ?? 0,
    activeResizeObservers: () => [...resizeObservers].filter((observer) => observer.active).length,
    activeIntersectionObservers: () =>
      [...intersectionObservers].filter((observer) => observer.active).length,
    dispose
  };
};

const followNames = [
  'ANCHOR_EPSILON_PX',
  'readAnchorRect',
  'toDocumentPoint',
  'hasMovedOnPage',
  'isAnchorOffscreen',
  'useAnchorFollow'
];
const panelNames = [
  'DEFAULT_ANCHOR_GAP_PX',
  'POSITION_EPSILON_PX',
  'isSamePlacement',
  'placeBelowAnchor',
  'useAnchoredPanel'
];

const loadFollow = (run) => loadDeclarations(followPath, followNames, run.bindings);

const loadPanel = (run) => {
  const follow = loadFollow(run);
  const exit = loadDeclarations(exitPath, ['DROPDOWN_EXIT_MS', 'useExitPresence'], run.bindings);
  return loadDeclarations(panelPath, panelNames, {
    ...run.bindings,
    ...follow,
    ...exit,
    clampToViewport
  });
};

const makeAnchor = (top = 300, left = 400, width = 100, height = 20) => {
  const anchor = {
    isConnected: true,
    rect: { top, left, bottom: top + height, right: left + width, width, height },
    getBoundingClientRect() {
      return this.rect;
    }
  };
  anchor.move = (nextTop, nextLeft = anchor.rect.left) => {
    anchor.rect = {
      ...anchor.rect,
      top: nextTop,
      left: nextLeft,
      bottom: nextTop + anchor.rect.height,
      right: nextLeft + anchor.rect.width
    };
  };
  return anchor;
};

test('anchor and panel changes publish before the next paint boundary', () => {
  const run = createHookRun();
  const { useAnchoredPanel } = loadPanel(run);
  const anchor = makeAnchor(500, 400);
  const panel = { offsetWidth: 160, offsetHeight: 100 };
  const anchorRef = { current: anchor };
  const panelRef = { current: null };
  let closes = 0;
  const props = {
    open: false,
    anchorRef,
    panelRef,
    onClose: () => {
      closes += 1;
    },
    gutter: 8,
    align: 'right',
    initialWidth: () => 160
  };
  run.setHook((options) => useAnchoredPanel(options));
  run.render(props);
  run.flushPassive();

  run.render({ ...props, open: true });
  assert.deepEqual(run.output.position, {
    top: 524,
    left: 340,
    openUpward: false,
    availableHeight: undefined
  });
  assert.deepEqual(run.warnings, [], 'opening layout work must not call flushSync');

  panelRef.current = panel;
  run.flushPassive();
  assert.equal(run.output.present, true);
  assert.equal(run.output.position.top, 396);
  run.runFrame();

  anchor.move(460);
  run.runFrame();
  assert.equal(run.output.position.top, 484, 'follow publication is visible to the same paint');

  panel.offsetHeight = 160;
  run.emitResize(panel);
  assert.equal(
    run.output.position.top,
    296,
    'panel resize publication is visible to the same paint'
  );
  assert.deepEqual(run.warnings, []);
  assert.deepEqual(run.observerErrors, []);
  assert.equal(closes, 0);
  run.dispose();
});

test('follow filters idle, sub-pixel, and document-scroll frames but recomputes on resize', () => {
  const run = createHookRun();
  const { useAnchorFollow } = loadFollow(run);
  const anchor = makeAnchor();
  const moves = [];
  const anchorRef = { current: anchor };
  run.setHook((options) => useAnchorFollow(options));
  run.render({ enabled: true, anchorRef, onAnchorMove: (rect) => moves.push(rect) });
  run.flushPassive();
  assert.equal(run.pendingFrames(), 1);
  assert.equal(run.windowListeners('resize'), 1);
  assert.equal(run.activeIntersectionObservers(), 1);

  run.runFrame();
  assert.equal(moves.length, 1, 'the first frame places the panel');
  run.runFrame();
  assert.equal(moves.length, 1, 'an idle frame does no work');

  anchor.move(300.25, 400.25);
  run.runFrame();
  assert.equal(moves.length, 1, 'sub-pixel noise stays below the movement threshold');

  run.window.scrollY = 100;
  anchor.move(200.25, 400.25);
  run.runFrame();
  assert.equal(moves.length, 1, 'pure document scroll preserves the document point');

  run.window.dispatch('resize');
  assert.equal(moves.length, 2, 'resize forces viewport clamp and flip recomputation');
  anchor.move(220.25, 400.25);
  run.runFrame();
  assert.equal(moves.length, 3, 'meaningful document movement publishes once');

  run.dispose();
  assert.equal(run.pendingFrames(), 0);
  assert.equal(run.windowListeners('resize'), 0);
  assert.equal(run.activeIntersectionObservers(), 0);
});

test('synchronous unmount during movement cannot queue a successor frame', () => {
  const run = createHookRun();
  const { useAnchorFollow } = loadFollow(run);
  const anchorRef = { current: makeAnchor() };
  run.setHook((options) => useAnchorFollow(options));
  run.render({ enabled: true, anchorRef, onAnchorMove: () => run.dispose() });
  run.flushPassive();
  run.runFrame();
  assert.equal(run.pendingFrames(), 0);
  assert.equal(run.windowListeners('resize'), 0);
  assert.equal(run.activeIntersectionObservers(), 0);
});

test('clipping and anchor loss close once and cleanup all follow resources', () => {
  for (const loss of ['clipped', 'disconnected', 'offscreen']) {
    const run = createHookRun();
    const { useAnchorFollow } = loadFollow(run);
    const anchor = makeAnchor();
    const anchorRef = { current: anchor };
    let closes = 0;
    run.setHook((options) => useAnchorFollow(options));
    run.render({
      enabled: true,
      anchorRef,
      onAnchorMove: () => undefined,
      onAnchorLost: () => {
        closes += 1;
      }
    });
    run.flushPassive();
    run.runFrame();
    if (loss === 'clipped') run.emitIntersection(false);
    if (loss === 'disconnected') {
      anchor.isConnected = false;
      run.runFrame();
    }
    if (loss === 'offscreen') {
      anchor.move(700);
      run.runFrame();
    }
    assert.equal(closes, 1, `${loss} anchor closes once`);
    if (run.pendingFrames() > 0) run.runFrame();
    assert.equal(closes, 1, `${loss} anchor stays closed`);
    run.dispose();
    assert.equal(run.pendingFrames(), 0);
    assert.equal(run.windowListeners('resize'), 0);
    assert.equal(run.activeIntersectionObservers(), 0);
  }
});

test('closing keeps follow active, reopening cancels exit, and final close releases resources', () => {
  const run = createHookRun();
  const { useAnchoredPanel } = loadPanel(run);
  const anchor = makeAnchor(500, 400);
  const panel = { offsetWidth: 160, offsetHeight: 100 };
  const anchorRef = { current: anchor };
  const panelRef = { current: panel };
  const openProps = {
    open: true,
    anchorRef,
    panelRef,
    onClose: () => undefined,
    gutter: 8,
    align: 'right'
  };
  run.setHook((options) => useAnchoredPanel(options));
  run.render(openProps);
  run.flushPassive();
  run.runFrame();

  run.render({ ...openProps, open: false });
  run.flushPassive();
  assert.equal(run.output.present, true);
  assert.equal(run.output.closing, true);
  assert.equal(run.pendingTimers(), 1);
  anchor.move(470);
  run.runFrame();
  assert.equal(run.output.position.top, 366, 'the exiting panel still follows its anchor');

  run.render(openProps);
  run.flushPassive();
  assert.equal(run.output.present, true);
  assert.equal(run.output.closing, false);
  assert.equal(run.pendingTimers(), 0, 'reopening cancels the pending unmount');
  assert.equal(run.pendingFrames(), 1);

  run.render({ ...openProps, open: false });
  run.flushPassive();
  run.advance(150);
  assert.equal(run.output.present, false);
  assert.equal(run.pendingFrames(), 0);
  assert.equal(run.pendingTimers(), 0);
  assert.equal(run.windowListeners('resize'), 0);
  assert.equal(run.documentListeners('keydown'), 0);
  assert.equal(run.activeResizeObservers(), 0);
  assert.equal(run.activeIntersectionObservers(), 0);
  assert.deepEqual(run.warnings, []);
  assert.deepEqual(run.observerErrors, []);
  run.dispose();
});
