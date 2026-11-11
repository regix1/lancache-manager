import assert from 'node:assert/strict';
import test, { afterEach } from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

const reactUrl = moduleUrl(`
  const same = (left, right) =>
    left && right && left.length === right.length && left.every((value, index) => Object.is(value, right[index]));
  export const runtime = {
    cursor: 0,
    pending: [],
    slots: [],
    begin() { this.cursor = 0; this.pending = []; },
    flush() {
      for (const item of this.pending.splice(0)) {
        const slot = this.slots[item.index];
        slot.cleanup?.();
        slot.cleanup = item.effect() ?? null;
      }
    },
    unmount() {
      for (const slot of this.slots) slot?.cleanup?.();
      this.cursor = 0;
      this.pending = [];
      this.slots = [];
    }
  };
  export const useRef = (initial) => {
    const index = runtime.cursor++;
    runtime.slots[index] ??= { current: initial };
    return runtime.slots[index];
  };
  export const useState = (initial) => {
    const index = runtime.cursor++;
    runtime.slots[index] ??= { value: typeof initial === 'function' ? initial() : initial };
    const setState = (value) => {
      const previous = runtime.slots[index].value;
      runtime.slots[index].value = typeof value === 'function' ? value(previous) : value;
    };
    return [runtime.slots[index].value, setState];
  };
  export const useCallback = (callback, dependencies) => {
    const index = runtime.cursor++;
    const previous = runtime.slots[index];
    if (!previous || !same(previous.dependencies, dependencies)) {
      runtime.slots[index] = { callback, dependencies };
    }
    return runtime.slots[index].callback;
  };
  export const useEffect = (effect, dependencies) => {
    const index = runtime.cursor++;
    const previous = runtime.slots[index];
    if (!previous || !same(previous.dependencies, dependencies)) {
      runtime.slots[index] = { cleanup: previous?.cleanup ?? null, dependencies };
      runtime.pending.push({ effect, index });
    }
  };
  export const useLayoutEffect = useEffect;
`);

// Same body as helpers.ts, so the fake window's matchMedia drives reduced motion.
const reducedMotionUrl = moduleUrl(
  "export const prefersReducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;"
);
const scrollGlideUrl = await compileToUrl('../src/utils/scrollGlide.ts', {
  '@components/features/management/status-check/helpers': reducedMotionUrl
});

const [{ useExpansionScroll }, { runtime }] = await Promise.all([
  import(
    await compileToUrl('../src/components/features/downloads/useExpansionScroll.ts', {
      react: reactUrl,
      '@utils/scrollGlide': scrollGlideUrl
    })
  ),
  import(reactUrl)
]);

// Each fake frame moves the fake clock by this much; the default 20 frames cover the 450 ms travel.
const FRAME_MS = 50;

afterEach(() => {
  delete globalThis.performance.now;
});

class Events {
  listeners = new Map();

  addEventListener(name, listener) {
    const listeners = this.listeners.get(name) ?? new Set();
    listeners.add(listener);
    this.listeners.set(name, listeners);
  }

  removeEventListener(name, listener) {
    this.listeners.get(name)?.delete(listener);
  }

  dispatch(name, event = {}) {
    for (const listener of this.listeners.get(name) ?? []) listener(event);
  }
}

const browser = () => {
  const events = new Events();
  const frames = new Map();
  const calls = [];
  const clock = { now: 1000 };
  let frameId = 0;
  let reduceMotion = false;

  const documentOwner = {
    clientHeight: 600,
    scrollHeight: 2400,
    scrollTop: 0,
    scrollTo({ top }) {
      this.scrollTop = top;
    }
  };
  const navigation = {
    getBoundingClientRect: () => ({ bottom: 48, height: 48, top: 0 })
  };
  const document = {
    scrollingElement: documentOwner,
    querySelector: (selector) => (selector === 'nav.sticky' ? navigation : null)
  };
  const window = {
    innerHeight: 600,
    addEventListener: (...args) => events.addEventListener(...args),
    removeEventListener: (...args) => events.removeEventListener(...args),
    getComputedStyle: () => ({ position: 'sticky' }),
    matchMedia: () => ({ matches: reduceMotion }),
    // A native smooth scroll is still in flight when a test reads the position, so it moves nothing.
    scrollTo({ top, behavior }) {
      calls.push({ behavior, owner: 'document', top });
      if (behavior !== 'smooth') documentOwner.scrollTop = top;
    }
  };

  globalThis.document = document;
  globalThis.window = window;
  globalThis.requestAnimationFrame = (callback) => {
    const id = ++frameId;
    frames.set(id, callback);
    return id;
  };
  globalThis.cancelAnimationFrame = (id) => frames.delete(id);
  Object.defineProperty(globalThis.performance, 'now', {
    configurable: true,
    value: () => clock.now
  });

  return {
    calls,
    clock,
    documentOwner,
    events,
    frames,
    setReduceMotion(value) {
      reduceMotion = value;
    }
  };
};

const virtualOwner = (rect = { bottom: 500, height: 400, top: 100 }) => ({
  clientHeight: rect.height,
  className: 'virtual-list-parent',
  getBoundingClientRect: () => rect,
  scrollHeight: 1800,
  scrollTop: 0,
  scrollTo({ top, behavior }) {
    this.calls.push({ behavior, owner: 'virtual', top });
    this.scrollTop = top;
  },
  calls: []
});

const group = ({ id = 'A', top, height, owner = null, animations = [] }) => ({
  dataset: { downloadGroupId: id },
  closest: (selector) => (selector === '.virtual-list-parent' ? owner : null),
  getAnimations: () => animations,
  getBoundingClientRect() {
    const scrollTop = owner ? owner.scrollTop : globalThis.document.scrollingElement.scrollTop;
    const currentTop = top - scrollTop;
    return { bottom: currentTop + height, height, top: currentTop };
  }
});

const content = (...targets) => ({
  current: {
    querySelectorAll: () => targets
  }
});

const advance = async (state, count = 20) => {
  for (let index = 0; index < count; index += 1) {
    const pending = [...state.frames.values()];
    state.frames.clear();
    state.clock.now += FRAME_MS;
    pending.forEach((callback) => callback(state.clock.now));
    await Promise.resolve();
    await Promise.resolve();
  }
};

const mountHook = (initial) => {
  runtime.unmount();
  let options = initial;
  let result;
  const render = (changes = {}) => {
    options = { ...options, ...changes };
    runtime.begin();
    result = useExpansionScroll(options);
    runtime.flush();
    return result;
  };
  render();
  return { render, result: () => result };
};

const baseOptions = (root) => ({
  contentRoot: root,
  enabled: true,
  expandedItem: null,
  membersReady: false,
  resetKey: 'page-1',
  view: 'normal'
});

test('document scrolling waits for readiness and uses the nearest fitting edge below navigation', async () => {
  const state = browser();
  const target = group({ top: 700, height: 200 });
  const hook = mountHook(baseOptions(content(target)));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 0, 'placeholder readiness cannot start movement');

  hook.render({ membersReady: true });
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 316);
  assert.equal(
    state.events.listeners.get('wheel')?.size ?? 0,
    0,
    'settled motion cleans up input listeners'
  );
});

test('an oversized row aligns its header and reduced motion moves immediately', async () => {
  const state = browser();
  state.setReduceMotion(true);
  const target = group({ top: 500, height: 800 });
  const hook = mountHook(baseOptions(content(target)));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  // The first pass finds no frame yet: the effect's promise chain asks for its first layout frame
  // only after a few awaits. The next two passes are the two layout frames.
  await advance(state, 3);
  // Let the promise chain after the second layout frame finish without running another frame.
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(state.documentOwner.scrollTop, 436, 'the two layout frames are enough to land');
  assert.equal(state.frames.size, 0, 'no travel frame is scheduled');
});

test('a fully visible row consumes the intent without scrolling', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 100, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state);

  assert.equal(state.documentOwner.scrollTop, 0);
});

test('a virtual list is the scroll owner and keeps its measurement ref path intact', async () => {
  const state = browser();
  const owner = virtualOwner();
  const target = group({ top: 600, height: 100, owner });
  const hook = mountHook(baseOptions(content(target)));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state);

  assert.equal(owner.scrollTop, 216);
  assert.equal(state.documentOwner.scrollTop, 0, 'the page stays put');
});

test('finite disclosure motion and two stable frames finish before scrolling', async () => {
  const state = browser();
  let finishAnimation;
  const finished = new Promise((resolve) => {
    finishAnimation = resolve;
  });
  const animation = {
    effect: { getComputedTiming: () => ({ endTime: 350 }) },
    finished
  };
  const hook = mountHook(
    baseOptions(content(group({ top: 700, height: 200, animations: [animation] })))
  );

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state, 4);
  assert.equal(state.documentOwner.scrollTop, 0);

  finishAnimation();
  await advance(state, 1);
  assert.equal(state.documentOwner.scrollTop, 0, 'one layout frame is not enough');
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 316);
});

test('wheel interruption consumes pending intent and cannot revive after data arrives', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  state.events.dispatch('wheel');
  hook.render({ membersReady: true });
  await advance(state);

  assert.equal(state.documentOwner.scrollTop, 0);
});

test('touch interruption consumes pending intent before readiness', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  state.events.dispatch('touchstart');
  hook.render({ membersReady: true });
  await advance(state);

  assert.equal(state.documentOwner.scrollTop, 0);
});

for (const [name, event] of [
  ['wheel', {}],
  ['pointerdown', {}],
  ['touchstart', {}],
  ['keydown', { key: 'PageDown' }]
]) {
  test(`${name} during the travel stops it where it is`, async () => {
    const state = browser();
    const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

    hook.result().requestScroll('A');
    hook.render({ expandedItem: 'A', membersReady: true });
    await advance(state, 4);
    assert.equal(state.events.listeners.get('wheel')?.size, 1, 'the travel is under way');

    // The input's own scroll lands here; the stop must leave it alone.
    state.documentOwner.scrollTop = 123;
    state.events.dispatch(name, event);
    assert.deepEqual(state.calls, [], 'the stop makes no scroll call');
    assert.equal(state.frames.size, 0, 'no further frame is scheduled');
    await advance(state);
    assert.equal(state.documentOwner.scrollTop, 123);
  });
}

test('navigation keys cancel a pending request before readiness', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  state.events.dispatch('keydown', { key: 'PageDown' });
  hook.render({ membersReady: true });
  await advance(state);

  assert.equal(state.documentOwner.scrollTop, 0);
});

test('A-B-A keeps only the latest explicit click intent', async () => {
  const state = browser();
  const targetA = group({ id: 'A', top: 700, height: 200 });
  const targetB = group({ id: 'B', top: 900, height: 200 });
  const hook = mountHook(baseOptions(content(targetA, targetB)));

  hook.result().requestScroll('A');
  hook.result().requestScroll('B');
  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state);

  assert.equal(state.documentOwner.scrollTop, 316);
});

test('collapse, view changes, setting changes, and remounts create no scroll', async () => {
  const state = browser();
  const target = group({ top: 700, height: 200 });
  const hook = mountHook(baseOptions(content(target)));

  hook.render({ expandedItem: 'A', membersReady: true });
  hook.render({ enabled: false });
  hook.render({ enabled: true, resetKey: 'page-2' });
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 0, 'state and setting effects never mint an intent');

  hook.result().requestScroll('A');
  hook.render({ expandedItem: null, membersReady: false });
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 0, 'collapse consumes the explicit intent');

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true, view: 'card' });
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 0, 'card view retains its no-auto-scroll behavior');

  hook.render({ view: 'normal', membersReady: false });
  hook.result().requestScroll('A');
  runtime.unmount();
  await advance(state);
  assert.equal(state.documentOwner.scrollTop, 0, 'unmount consumes pending intent');
});
