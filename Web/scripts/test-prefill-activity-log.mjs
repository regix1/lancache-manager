import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test, { mock } from 'node:test';
import typescript from 'typescript';
import { MemoryStorage, compileToUrl, moduleUrl } from './transpile-module.mjs';

// The provider runs on a small hook runtime that keeps React's update order: a state update made while
// no other update is queued is computed at once, and every later one waits for the next render.
const runtime = {
  slots: [],
  index: 0,
  queued: false,
  effects: [],
  render(component, props) {
    this.index = 0;
    this.effects = [];
    const element = component(props);
    this.queued = false;
    for (const effect of this.effects) {
      effect.slot.cleanup?.();
      effect.slot.cleanup = effect.run() ?? null;
    }
    return element;
  },
  slot(create) {
    if (this.slots.length <= this.index) this.slots.push(create());
    return this.slots[this.index++];
  }
};
const sameDeps = (left, right) =>
  left !== undefined &&
  left.length === right.length &&
  left.every((value, i) => Object.is(value, right[i]));
globalThis.prefillHookRuntime = {
  useState(initial) {
    const slot = runtime.slot(() => ({
      value: typeof initial === 'function' ? initial() : initial,
      updates: []
    }));
    for (const update of slot.updates.splice(0)) {
      slot.value = update.eager
        ? update.value
        : typeof update.action === 'function'
          ? update.action(slot.value)
          : update.action;
    }
    slot.set ??= (action) => {
      if (!runtime.queued) {
        slot.updates.push({
          eager: true,
          value: typeof action === 'function' ? action(slot.value) : action
        });
      } else slot.updates.push({ eager: false, action });
      runtime.queued = true;
    };
    return [slot.value, slot.set];
  },
  useRef(initial) {
    return runtime.slot(() => ({ current: initial }));
  },
  useCallback(callback, deps) {
    const slot = runtime.slot(() => ({}));
    if (!sameDeps(slot.deps, deps)) {
      slot.deps = deps;
      slot.callback = callback;
    }
    return slot.callback;
  },
  useEffect(run, deps) {
    const slot = runtime.slot(() => ({ cleanup: null }));
    if (!sameDeps(slot.deps, deps)) {
      slot.deps = deps;
      runtime.effects.push({ slot, run });
    }
  }
};

const store = new MemoryStorage();
const sessionStore = {
  getItem: (key) => store.getItem(key),
  setItem: (key, value) => store.setItem(key, value),
  removeItem: (key) => store.removeItem(key),
  getJSON: (key) => {
    const value = store.getItem(key);
    return value === null ? null : JSON.parse(value);
  },
  setJSON: (key, value) => {
    store.setItem(key, JSON.stringify(value));
    return true;
  }
};
globalThis.prefillSessionStore = sessionStore;

const reactUrl = moduleUrl(`
const hooks = globalThis.prefillHookRuntime;
export const useState = (initial) => hooks.useState(initial);
export const useRef = (initial) => hooks.useRef(initial);
export const useCallback = (callback, deps) => hooks.useCallback(callback, deps);
export const useEffect = (run, deps) => hooks.useEffect(run, deps);
export const createContext = () => ({ Provider: 'Provider' });
export default { createElement: (type, props) => ({ type, props }) };
`);
const contextSource = await readFile(
  new URL('../src/contexts/PrefillContext.tsx', import.meta.url),
  'utf8'
);
const aliases = {
  react: reactUrl,
  '@components/features/prefill/ActivityLog.utils': await compileToUrl(
    '../src/components/features/prefill/ActivityLog.utils.ts'
  ),
  '@components/features/prefill/hooks/prefillTypes': await compileToUrl(
    '../src/components/features/prefill/hooks/prefillTypes.ts'
  ),
  './PrefillContext.types': moduleUrl(`export const PrefillContext = { Provider: 'Provider' };`),
  '@utils/constants': moduleUrl(
    `export const STORAGE_KEYS = { PREFILL_SESSION_ID: 'prefill_session_id', PREFILL_IN_PROGRESS: 'prefill_in_progress' };`
  ),
  '@utils/storage': moduleUrl(`export const sessionStore = globalThis.prefillSessionStore;`)
};
const compiled = Object.entries(aliases).reduce(
  (text, [alias, url]) => text.split(`from '${alias}'`).join(`from '${url}'`),
  typescript.transpileModule(contextSource, {
    fileName: 'PrefillContext.tsx',
    compilerOptions: {
      module: typescript.ModuleKind.ESNext,
      target: typescript.ScriptTarget.ES2022,
      jsx: typescript.JsxEmit.React
    }
  }).outputText
);
const prefillContext = await import(moduleUrl(compiled));

const LOG_KEY = 'prefill_activity_log';

test('a cleared activity log is never written back to storage, and a new line still is', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const render = () =>
      runtime.render(prefillContext.PrefillProvider, { children: null }).props.value;
    let context = render();
    // Two lines arrive before the page renders again, then the session ends.
    context.addLog('info', 'Prefill started');
    context.addLog('warning', 'The session ended');
    context.clearAllPrefillStorage();
    context = render();
    mock.timers.tick(500);
    assert.equal(store.getItem(LOG_KEY), null, 'the ended session log stays removed');
    assert.deepEqual(context.logEntries, []);

    context.addLog('info', 'Next session line');
    context = render();
    mock.timers.tick(500);
    assert.deepEqual(
      JSON.parse(store.getItem(LOG_KEY)).map((entry) => entry.message),
      ['Next session line']
    );

    // Clear log from the page's own button: same rule.
    context.addLog('info', 'Line before clear');
    context.clearLogs();
    context = render();
    mock.timers.tick(500);
    assert.equal(store.getItem(LOG_KEY), null, 'a cleared log stays removed');
  } finally {
    mock.timers.reset();
  }
});
