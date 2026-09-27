import assert from 'node:assert/strict';
import test from 'node:test';
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

const [{ useExpansionScroll }, { runtime }] = await Promise.all([
  import(
    await compileToUrl('../src/components/features/downloads/useExpansionScroll.ts', {
      react: reactUrl
    })
  ),
  import(reactUrl)
]);

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
  let frameId = 0;
  let reduceMotion = false;
  let smoothMoves = true;

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
    scrollTo({ top, behavior }) {
      calls.push({ behavior, owner: 'document', top });
      if (behavior !== 'smooth' || smoothMoves) documentOwner.scrollTop = top;
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

  return {
    calls,
    documentOwner,
    events,
    frames,
    setReduceMotion(value) {
      reduceMotion = value;
    },
    setSmoothMoves(value) {
      smoothMoves = value;
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

const advance = async (state, count = 8) => {
  for (let index = 0; index < count; index += 1) {
    const pending = [...state.frames.values()];
    state.frames.clear();
    pending.forEach((callback) => callback(index));
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
  assert.deepEqual(state.calls, [], 'placeholder readiness cannot start movement');

  hook.render({ membersReady: true });
  await advance(state);
  assert.deepEqual(state.calls[0], { behavior: 'smooth', owner: 'document', top: 316 });
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
  await advance(state);

  assert.deepEqual(state.calls[0], { behavior: 'auto', owner: 'document', top: 436 });
});

test('a fully visible row consumes the intent without scrolling', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 100, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state);

  assert.deepEqual(state.calls, []);
});

test('a virtual list is the scroll owner and keeps its measurement ref path intact', async () => {
  const state = browser();
  const owner = virtualOwner();
  const target = group({ top: 600, height: 100, owner });
  const hook = mountHook(baseOptions(content(target)));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state);

  assert.deepEqual(owner.calls[0], { behavior: 'smooth', owner: 'virtual', top: 216 });
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
  assert.deepEqual(state.calls, []);

  finishAnimation();
  await advance(state, 1);
  assert.deepEqual(state.calls, [], 'one layout frame is not enough');
  await advance(state, 5);
  assert.equal(state.calls.length, 1);
});

test('wheel interruption consumes pending intent and cannot revive after data arrives', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  state.events.dispatch('wheel');
  hook.render({ membersReady: true });
  await advance(state);

  assert.deepEqual(state.calls, []);
});

test('touch interruption consumes pending intent before readiness', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  state.events.dispatch('touchstart');
  hook.render({ membersReady: true });
  await advance(state);

  assert.deepEqual(state.calls, []);
});

test('pointer interruption stops native movement at its current position', async () => {
  const state = browser();
  state.setSmoothMoves(false);
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true });
  await advance(state, 4);
  assert.equal(state.calls[0].behavior, 'smooth');

  state.documentOwner.scrollTop = 123;
  state.events.dispatch('pointerdown');
  assert.deepEqual(state.calls.at(-1), { behavior: 'auto', owner: 'document', top: 123 });
});

test('navigation keys cancel a pending request before readiness', async () => {
  const state = browser();
  const hook = mountHook(baseOptions(content(group({ top: 700, height: 200 }))));

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A' });
  state.events.dispatch('keydown', { key: 'PageDown' });
  hook.render({ membersReady: true });
  await advance(state);

  assert.deepEqual(state.calls, []);
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

  assert.equal(state.calls.length, 1);
  assert.equal(state.calls[0].top, 316);
});

test('collapse, view changes, setting changes, and remounts create no scroll', async () => {
  const state = browser();
  const target = group({ top: 700, height: 200 });
  const hook = mountHook(baseOptions(content(target)));

  hook.render({ expandedItem: 'A', membersReady: true });
  hook.render({ enabled: false });
  hook.render({ enabled: true, resetKey: 'page-2' });
  await advance(state);
  assert.deepEqual(state.calls, [], 'state and setting effects never mint an intent');

  hook.result().requestScroll('A');
  hook.render({ expandedItem: null, membersReady: false });
  await advance(state);
  assert.deepEqual(state.calls, [], 'collapse consumes the explicit intent');

  hook.result().requestScroll('A');
  hook.render({ expandedItem: 'A', membersReady: true, view: 'card' });
  await advance(state);
  assert.deepEqual(state.calls, [], 'card view retains its no-auto-scroll behavior');

  hook.render({ view: 'normal', membersReady: false });
  hook.result().requestScroll('A');
  runtime.unmount();
  await advance(state);
  assert.deepEqual(state.calls, [], 'unmount consumes pending intent');
});
