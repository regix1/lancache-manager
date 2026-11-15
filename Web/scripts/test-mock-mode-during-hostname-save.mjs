import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * The Clients section saves client-name lookup in two steps, each a POST followed by a full reverse
 * DNS read, and each step runs the `setEnabled` or `setSettings` handed out by the render it started
 * in. When mock mode turns on in Settings while one of those POSTs is still out, the step that
 * finishes afterwards must not start a real read or put real names, the lookup switch or a real
 * failure on the mock page.
 *
 * The shipped provider runs on a small hook store that keeps effect and callback dependencies, with
 * every POST and read held until the test answers it.
 */

const HOSTNAMES = 'src/contexts/ClientHostnameContext.tsx';
const LAN_ADDRESS = '192.168.1.40';
const LAN_HOSTNAME = 'living-room-pc.lan';
const SETTINGS = {
  resolver: '192.168.1.1',
  guestAccess: false,
  routerLookup: true,
  dockerLookup: true
};
const SERVER_LOOKUP = {
  enabled: true,
  hostnames: { [LAN_ADDRESS]: LAN_HOSTNAME },
  reason: 'none',
  settings: SETTINGS
};

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

/** A promise the test settles by hand. */
const held = () => {
  let resolve;
  const promise = new Promise((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
};

/**
 * Mounts the provider for a signed-in admin with mock mode off.
 *
 * @returns {{ shown: () => Record<string, unknown>, reads: Array<{ resolve: Function }>,
 *   saves: Array<{ resolve: Function }>, setMockMode: (on: boolean) => Promise<void>,
 *   settle: () => Promise<void> }} What the provider hands the screens after a fresh draw, the
 *   hostname reads and the POSTs still out, a Settings toggle of mock mode and a wait for pending work.
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
  const saves = [];
  const heldCall = (list) => () => {
    const call = held();
    list.push(call);
    return call.promise;
  };
  const mock = { mockMode: false };
  const Hostnames = make(HOSTNAMES, 'ClientHostnameProvider', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    ApiService: {
      getClientHostnames: heldCall(reads),
      setClientHostnameLookup: heldCall(saves),
      setClientHostnameSettings: heldCall(saves)
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
    saves,
    settle,
    setMockMode: async (on) => {
      mock.mockMode = on;
      await settle();
    }
  };
};

const STEPS = {
  'turning the lookup on': {
    start: (value) => value.setEnabled(true),
    answer: { enabled: true }
  },
  'saving the lookup settings': {
    start: (value) => value.setSettings(SETTINGS),
    answer: SETTINGS
  }
};

test('a hostname save still out when mock mode turns on puts nothing real on the mock page', async () => {
  const wrong = [];
  for (const [step, { start, answer }] of Object.entries(STEPS)) {
    const page = mountHostnames();
    page.shown();
    await page.settle();
    assert.equal(page.reads.length, 1, `${step}: the signed-in admin started no hostname read`);
    page.reads.shift().resolve(SERVER_LOOKUP);
    await page.settle();
    assert.equal(
      page.shown().getHostnameForIp(LAN_ADDRESS),
      LAN_HOSTNAME,
      `${step}: no real names`
    );

    // The save starts with mock mode off, then mock mode turns on before its POST answers.
    const saving = start(page.shown());
    assert.equal(page.saves.length, 1, `${step}: the save sent no POST`);
    await page.setMockMode(true);
    page.saves.shift().resolve(answer);
    await page.settle();

    // Whatever read the finished step sent answers with the real names, as the server would.
    const readsAfterMockOn = page.reads.length;
    for (const read of page.reads.splice(0)) read.resolve(SERVER_LOOKUP);
    await saving;
    await page.settle();

    const after = page.shown();
    if (readsAfterMockOn !== 0)
      wrong.push(`${step}: the mock page started ${readsAfterMockOn} real hostname read(s)`);
    const name = after.getHostnameForIp(LAN_ADDRESS);
    if (name !== null) wrong.push(`${step}: the mock page labels ${LAN_ADDRESS} "${name}"`);
    if (after.enabled !== false)
      wrong.push(`${step}: the mock page shows the lookup ${after.enabled}`);
    if (after.error !== null)
      wrong.push(`${step}: the mock page shows the failure "${after.error}"`);
    if (after.loading !== false) wrong.push(`${step}: the mock page shows a spinner`);
  }

  assert.deepEqual(wrong, [], `a save finished after mock mode turned on: ${wrong.join('; ')}`);
});
