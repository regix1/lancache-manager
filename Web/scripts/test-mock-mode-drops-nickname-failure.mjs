import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Turning mock mode on swaps the nickname list for generated groups. A real read that failed before
 * the switch, or one still out when it happens, belongs to the real list: the generated groups must
 * not sit under the real list's "failed to load" box, and a real answer that lands after the switch
 * must not replace them.
 *
 * The shipped provider runs here with the shipped load lifecycle inside it, on a small hook store that
 * keeps effect and callback dependencies, and every real read is held until the test answers it. The
 * test reads the `error` and `clientGroups` the provider hands the screens.
 */

const GROUPS = 'src/contexts/ClientGroupContext.tsx';
const LOAD_LIFECYCLE = 'src/hooks/useLoadLifecycle.ts';
const MESSAGE = 'The read failed';
const MOCK_GROUPS = [{ id: 1, nickname: 'Generated', memberIps: ['10.0.0.1'] }];
const SERVER_GROUPS = [{ id: 7, nickname: 'Office', memberIps: ['192.168.1.20'] }];

/** Draws nothing; the provider's children are not under test. */
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

/** The nickname provider for a signed-in admin, with mock mode the test switches. */
const mountGroups = () => {
  const store = hookStore();
  const reads = [];
  const mock = { mockMode: false };
  const managerLoading = {
    isLoading: false,
    beginLoad: () => undefined,
    markLoaded: () => undefined,
    markFailed: () => undefined
  };
  const Groups = make(GROUPS, 'ClientGroupProvider', {
    ...Object.fromEntries(importedNames(GROUPS).map((imported) => [imported, stub(imported)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useAuth: () => ({ authMode: 'authenticated', isLoading: false }),
    useMockMode: () => mock,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: false }),
    useReconnectRefetch: () => undefined,
    useManagerLoading: () => managerLoading,
    useTimeoutCallback: () => () => undefined,
    MockDataService: { generateMockClientGroups: () => MOCK_GROUPS },
    ApiService: {
      getClientGroups: (signal) =>
        new Promise((resolve, reject) => {
          signal?.addEventListener('abort', () => {
            const aborted = new Error('aborted');
            aborted.name = 'AbortError';
            reject(aborted);
          });
          reads.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
        })
    },
    ClientGroupContext: { Provider: stub('ClientGroupContext') },
    useLoadLifecycle: make(LOAD_LIFECYCLE, 'useLoadLifecycle', {
      ...store.hooks,
      getErrorMessage: (error) => error.message,
      isAbortError: (error) => error?.name === 'AbortError'
    })
  });
  const shown = () => store.render(() => Groups({ children: null })).props.value;
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
    setMockMode: async (on) => {
      mock.mockMode = on;
      await settle();
    }
  };
};

test('turning mock mode on drops the real list failure and any real read still out', async () => {
  const consoleError = console.error;
  console.error = () => undefined;
  const wrong = [];
  try {
    // A real read fails, then mock mode turns on.
    const failed = mountGroups();
    failed.shown();
    await failed.settle();
    assert.equal(failed.reads.length, 1, 'the signed-in admin started no read');
    failed.reads.shift().fail(MESSAGE);
    await failed.settle();
    assert.equal(failed.shown().error, MESSAGE, 'the failed read shows no failure');
    await failed.setMockMode(true);
    const afterFailure = failed.shown();
    if (afterFailure.error !== null)
      wrong.push(`after a failed read, the generated groups show "${afterFailure.error}"`);
    if (afterFailure.clientGroups !== MOCK_GROUPS)
      wrong.push('after a failed read, the generated groups are not the list shown');

    // A real read is still out when mock mode turns on, and answers after the switch.
    const pending = mountGroups();
    pending.shown();
    await pending.settle();
    assert.equal(pending.reads.length, 1, 'the signed-in admin started no read');
    const outstanding = pending.reads.shift();
    await pending.setMockMode(true);
    outstanding.succeed(SERVER_GROUPS);
    await pending.settle();
    const afterAnswer = pending.shown();
    if (afterAnswer.clientGroups !== MOCK_GROUPS)
      wrong.push(
        `a real read that answered after the switch replaced the generated groups with ${JSON.stringify(afterAnswer.clientGroups)}`
      );
  } finally {
    console.error = consoleError;
  }

  assert.deepEqual(wrong, [], `mock mode kept the real list's state: ${wrong.join('; ')}`);
});
