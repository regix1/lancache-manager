import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * The client-hostname settings and the nickname list each show a "failed to load" box while their
 * last read failed. When that viewer signs out and someone signs in with a password on the same page,
 * the new session starts its own read, and the failure the previous session saw is not this
 * session's: until the new read answers, neither surface may show the old box.
 *
 * The shipped providers run here on a small hook store that keeps effect and callback dependencies,
 * with the shipped load lifecycle inside the nickname provider and every read held until the test
 * answers it. The test reads the `error` each provider hands the screens that draw the box.
 */

const HOSTNAMES = 'src/contexts/ClientHostnameContext.tsx';
const GROUPS = 'src/contexts/ClientGroupContext.tsx';
const LOAD_LIFECYCLE = 'src/hooks/useLoadLifecycle.ts';
const MESSAGE = 'The read failed';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name `path` imports, so a stub stands in for any the test does not bind itself. */
const importedNames = (path) =>
  parseSource(path, typescript.ScriptKind.TSX)
    .statements.filter(typescript.isImportDeclaration)
    .flatMap((declaration) => {
      const clause = declaration.importClause;
      if (!clause || clause.isTypeOnly) return [];
      const names = clause.name ? [clause.name.text] : [];
      const bindings = clause.namedBindings;
      if (bindings && typescript.isNamedImports(bindings))
        names.push(
          ...bindings.elements
            .filter((element) => !element.isTypeOnly)
            .map((element) => element.name.text)
        );
      return names;
    });

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** Whether a dependency list changed since the last render. */
const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/** One mounted component's hooks; `render` draws it and runs the effects whose dependencies moved. */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const slot = (slots[cursor++] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => (slots[cursor++] ??= { current: initial }),
    useMemo: (build, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!('value' in slot) || changed(slot.deps, deps)) {
        slot.value = build();
        slot.deps = deps;
      }
      return slot.value;
    },
    useCallback: (callback, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.callback || changed(slot.deps, deps)) {
        slot.callback = callback;
        slot.deps = deps;
      }
      return slot.callback;
    },
    useEffect: (effect, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.ran || changed(slot.deps, deps)) {
        slot.ran = true;
        slot.deps = deps;
        effects.push({ slot, effect });
      }
    }
  };
  hooks.useLayoutEffect = hooks.useEffect;
  const render = (draw) => {
    cursor = 0;
    effects = [];
    const tree = draw();
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
    return tree;
  };
  return { hooks, render };
};

/**
 * Mounts one provider for a viewer whose sign-in state the test changes.
 *
 * @param {string} path The provider's source file.
 * @param {string} name The provider component.
 * @param {(hooks: object, reads: object) => object} bindings The names that file needs beyond the
 *   shared ones, given the provider's hooks and its held reads.
 * @returns {{ shown: () => Record<string, unknown>, reads: Array<{ succeed: Function, fail: Function }>,
 *   signIn: (mode: string) => Promise<void> }} What the provider hands the screens after a fresh
 *   draw, the reads still out, and a change of who is signed in.
 */
const mountContext = (path, name, bindings) => {
  const store = hookStore();
  const reads = [];
  const heldRead = () =>
    new Promise((resolve, reject) => {
      reads.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
    });
  const auth = { authMode: 'authenticated', isLoading: false };
  const signalR = { on: () => undefined, off: () => undefined, isConnected: false };
  const Context = make(path, name, {
    ...Object.fromEntries(importedNames(path).map((imported) => [imported, stub(imported)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useAuth: () => auth,
    useMockMode: () => ({ mockMode: false }),
    useSignalR: () => signalR,
    useReconnectRefetch: () => undefined,
    getErrorMessage: (error) => error.message,
    ...bindings(store.hooks, heldRead)
  });
  const shown = () => store.render(() => Context({ children: null })).props.value;
  const settle = async () => {
    for (let step = 0; step < 6; step += 1) {
      await turn();
      shown();
    }
  };
  return {
    shown,
    reads,
    settle,
    signIn: async (mode) => {
      auth.authMode = mode;
      await settle();
    }
  };
};

const managerLoading = {
  isLoading: false,
  beginLoad: () => undefined,
  markLoaded: () => undefined,
  markFailed: () => undefined
};
const scheduleReload = () => undefined;

const SURFACES = {
  'the client-hostname settings': () =>
    mountContext(HOSTNAMES, 'ClientHostnameProvider', (_hooks, heldRead) => ({
      ApiService: { getClientHostnames: heldRead },
      ClientHostnameContext: { Provider: stub('ClientHostnameContext') }
    })),
  'the nickname list': () =>
    mountContext(GROUPS, 'ClientGroupProvider', (hooks, heldRead) => ({
      ApiService: { getClientGroups: heldRead },
      ClientGroupContext: { Provider: stub('ClientGroupContext') },
      useManagerLoading: () => managerLoading,
      useTimeoutCallback: () => scheduleReload,
      useLoadLifecycle: make(LOAD_LIFECYCLE, 'useLoadLifecycle', {
        ...hooks,
        getErrorMessage: (error) => error.message,
        isAbortError: (error) => error?.name === 'AbortError'
      })
    }))
};

test('after a failed read, a sign-out and a password sign-in, neither surface shows the old failure', async () => {
  const consoleError = console.error;
  console.error = () => undefined;
  const stale = [];
  try {
    for (const [surface, mount] of Object.entries(SURFACES)) {
      const page = mount();
      page.shown();
      await page.settle();

      // The signed-in viewer's read fails, so the surface shows its "failed to load" box.
      assert.equal(page.reads.length, 1, `${surface}: the signed-in viewer started no read`);
      page.reads.shift().fail(MESSAGE);
      await page.settle();
      assert.equal(page.shown().error, MESSAGE, `${surface}: the failed read shows no failure`);

      // That viewer signs out, and a password sign-in follows on the same page.
      await page.signIn('unauthenticated');
      await page.signIn('authenticated');
      assert.equal(page.reads.length, 1, `${surface}: the new session started no read of its own`);

      const whileReading = page.shown().error;
      if (whileReading !== null)
        stale.push(`${surface} shows "${whileReading}" while the new session's first read is out`);
    }
  } finally {
    console.error = consoleError;
  }

  assert.deepEqual(
    stale,
    [],
    `the previous session's failure is still on screen after the sign-in: ${stale.join('; ')}`
  );
});
