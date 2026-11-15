import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Turning mock mode off in Settings starts a real client-hostname read, which waits on reverse DNS
 * and can take seconds (`Web/src/contexts/ClientHostnameContext.tsx:41-43`). Turning mock mode back
 * on before it answers puts the page on generated data, and mock mode never shows real names or a
 * real failure (`:49-50`). So whatever that read answers, and whenever it answers, the mock page keeps
 * raw addresses, no "failed to load" box and no spinner.
 *
 * The shipped provider runs on a small hook store that keeps effect and callback dependencies, the
 * way `test-sign-in-clears-previous-load-failure.mjs` mounts it, with every read held until the test
 * answers it. The steps run in both orders: mock mode on before the read answers, and after.
 */

const HOSTNAMES = 'src/contexts/ClientHostnameContext.tsx';
const LAN_ADDRESS = '192.168.1.40';
const LAN_HOSTNAME = 'living-room-pc.lan';
const FAILURE = 'Reverse lookup failed';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

const importedNames = parseSource(HOSTNAMES, typescript.ScriptKind.TSX)
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

const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/**
 * Mounts the provider for a signed-in admin with mock mode on.
 *
 * @returns {{ shown: () => Record<string, unknown>, reads: Array<{ succeed: Function, fail: Function }>,
 *   setMockMode: (on: boolean) => Promise<void>, settle: () => Promise<void> }} What the provider hands
 *   the screens after a fresh draw, the reads still out, a Settings toggle of mock mode and a wait for
 *   pending work.
 */
const mountHostnames = () => {
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

  const reads = [];
  const mock = { mockMode: true };
  const Hostnames = make(HOSTNAMES, 'ClientHostnameProvider', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    ApiService: {
      getClientHostnames: () =>
        new Promise((resolve, reject) => {
          reads.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
        })
    },
    useAuth: () => ({ authMode: 'authenticated', isLoading: false }),
    useMockMode: () => mock,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: false }),
    useReconnectRefetch: () => undefined,
    getErrorMessage: (error) => error.message,
    ClientHostnameContext: { Provider: stub('ClientHostnameContext') }
  });

  const shown = () => {
    cursor = 0;
    effects = [];
    const value = Hostnames({ children: null }).props.value;
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
    return value;
  };
  const settle = async () => {
    for (let draw = 0; draw < 6; draw += 1) {
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

const ANSWERS = {
  'answers with the real names': (read) =>
    read.succeed({
      enabled: true,
      hostnames: { [LAN_ADDRESS]: LAN_HOSTNAME },
      reason: 'none',
      settings: { resolver: null, guestAccess: false, routerLookup: true, dockerLookup: true }
    }),
  fails: (read) => read.fail(FAILURE)
};

test('a real hostname read that is out when mock mode turns on never lands on the mock page', async () => {
  const consoleError = console.error;
  console.error = () => undefined;
  const wrong = [];
  try {
    for (const [answered, answer] of Object.entries(ANSWERS)) {
      for (const mockOnFirst of [true, false]) {
        const page = mountHostnames();
        page.shown();
        await page.settle();
        assert.equal(page.reads.length, 0, 'the mock page started a real hostname read');

        // Mock mode off in Settings starts the real read.
        await page.setMockMode(false);
        assert.equal(page.reads.length, 1, 'turning mock mode off started no hostname read');
        const read = page.reads.shift();

        const steps = [
          ['mock mode on', () => page.setMockMode(true)],
          [
            `the read ${answered}`,
            async () => {
              answer(read);
              await page.settle();
            }
          ]
        ];
        if (!mockOnFirst) steps.reverse();
        for (const [, run] of steps) await run();

        const after = page.shown();
        const label = `${steps.map(([name]) => name).join(' then ')}`;
        const name = after.getHostnameForIp(LAN_ADDRESS);
        if (name !== null) wrong.push(`${label}: the mock page labels ${LAN_ADDRESS} "${name}"`);
        if (after.enabled !== false)
          wrong.push(`${label}: the mock page shows the lookup ${after.enabled}`);
        if (after.error !== null)
          wrong.push(`${label}: the mock page shows the failure "${after.error}"`);
        if (after.loading !== false) wrong.push(`${label}: the mock page shows a spinner`);
      }
    }
  } finally {
    console.error = consoleError;
  }

  assert.deepEqual(
    wrong,
    [],
    `a real hostname read reached the page after mock mode turned on: ${wrong.join('; ')}`
  );
});
