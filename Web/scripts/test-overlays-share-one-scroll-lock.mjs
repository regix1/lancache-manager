import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore } from './component-hooks.mjs';
import { featureTree } from './component-tree.mjs';
import { compileTree, make, ordersOf } from './transpile-module.mjs';

/**
 * A setup gate, a modal and a drawer each lock the page's scroll (`modal-open` on `<html>`) while
 * they are drawn, and they overlap: a gate fades out while the next screen's gate opens, a modal
 * opens on the page a gate fades onto, a drawer and a modal are up together. Whichever opens or
 * closes first, the page behind stays locked while any of them is still up, and scrolls again once
 * the last one has gone.
 *
 * The shipped components run here on one hook store each, with a fake document and a clock the test
 * advances, so every order of opening and closing two overlays is driven step by step.
 */

/** The page's `<html>` and `#root`, and the listeners a drawer adds. */
const fakeDocument = () => {
  const classes = new Set();
  const attributes = new Map();
  return {
    classes,
    activeElement: null,
    body: {},
    getElementById: (id) =>
      id === 'root'
        ? {
            inert: false,
            setAttribute: (name, value) => attributes.set(name, String(value)),
            getAttribute: (name) => (attributes.has(name) ? attributes.get(name) : null),
            removeAttribute: (name) => attributes.delete(name)
          }
        : null,
    documentElement: {
      classList: {
        add: (name) => classes.add(name),
        remove: (name) => classes.delete(name),
        contains: (name) => classes.has(name)
      }
    },
    contains: () => false,
    addEventListener: () => undefined,
    removeEventListener: () => undefined
  };
};

/** Timers the components start, held until the test runs them. */
const clock = { pending: new Map(), next: 1 };
const fakeSetTimeout = (callback) => {
  const id = clock.next++;
  clock.pending.set(id, callback);
  return id;
};
const fakeClearTimeout = (id) => clock.pending.delete(id);
/** Runs `step` with the fake clock in place of the real one. */
const onFakeClock = (step) => {
  const shipped = { setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
  globalThis.setTimeout = fakeSetTimeout;
  globalThis.clearTimeout = fakeClearTimeout;
  try {
    return step();
  } finally {
    globalThis.setTimeout = shipped.setTimeout;
    globalThis.clearTimeout = shipped.clearTimeout;
  }
};

// Each drawn overlay keeps its own hook slots; the hooks the components call go to the one drawing.
let drawing = null;
const hookNames = ['useState', 'useRef', 'useMemo', 'useCallback', 'useEffect', 'useLayoutEffect'];
const hooks = {
  ...Object.fromEntries(hookNames.map((name) => [name, (...args) => drawing.hooks[name](...args)])),
  useId: () => 'overlay-title'
};
const { MODAL_EXIT_MS } = await import(await compileTree('../src/hooks/useExitPresence.ts'));
const shared = {
  React: { ...React, ...hooks },
  ...hooks,
  createPortal: (node) => node,
  useTranslation: () => ({ t: (key) => key }),
  MODAL_EXIT_MS,
  useExitPresence: make('src/hooks/useExitPresence.ts', 'useExitPresence', hooks)
};
const components = {
  gate: (await featureTree('src/components/modals/SetupGate.tsx', 'SetupGate', shared)).Root,
  modal: (await featureTree('src/components/ui/Modal.tsx', 'Modal', shared)).Root,
  drawer: (await featureTree('src/components/ui/Drawer.tsx', 'Drawer', shared)).Root
};
const propsOf = {
  gate: { maxWidth: 'xl', icon: null, title: 'Sign in', children: null },
  modal: { onClose: () => undefined, title: 'Dialog', children: null },
  drawer: { onClose: () => undefined, title: 'Menu', children: null }
};

/** One overlay of `kind`, drawn on its own hook store with whatever `opened` it was last given. */
const overlay = (kind, label) => {
  const store = hookStore();
  const state = { label, opened: false };
  state.draw = () => {
    drawing = store;
    onFakeClock(() =>
      store.draw(() => components[kind]({ ...propsOf[kind], opened: state.opened }))
    );
  };
  return state;
};

/**
 * Draws every overlay, then runs each timer they started until none is left, as time passing would.
 * Each round draws three times, so a value an effect sets is drawn and the effects it changes run.
 */
const settle = (overlays) => {
  for (let round = 0; round < 20; round += 1) {
    for (let pass = 0; pass < 3; pass += 1) overlays.forEach((item) => item.draw());
    if (clock.pending.size === 0) return;
    const due = [...clock.pending.values()];
    clock.pending.clear();
    onFakeClock(() => due.forEach((callback) => callback()));
  }
  assert.fail('the overlays kept starting timers');
};

const pairs = [
  ['gate', 'gate'],
  ['gate', 'modal'],
  ['gate', 'drawer'],
  ['modal', 'drawer']
];

test('the page behind stays scroll-locked while any overlay is up, in every order two of them open and close', () => {
  const shippedDocument = globalThis.document;
  const shippedElement = globalThis.HTMLElement;
  globalThis.HTMLElement = class {};
  const wrong = [];
  let orders = 0;
  try {
    for (const [first, second] of pairs) {
      const steps = [
        { overlay: 0, opened: true },
        { overlay: 1, opened: true },
        { overlay: 0, opened: false },
        { overlay: 1, opened: false }
      ];
      const valid = ordersOf(steps).filter((order) =>
        [0, 1].every(
          (index) =>
            order.findIndex((step) => step.overlay === index && step.opened) <
            order.findIndex((step) => step.overlay === index && !step.opened)
        )
      );
      for (const order of valid) {
        orders += 1;
        const document = fakeDocument();
        globalThis.document = document;
        const overlays = [overlay(first, `${first} A`), overlay(second, `${second} B`)];
        const done = [];
        try {
          for (const step of order) {
            overlays[step.overlay].opened = step.opened;
            done.push(`${step.opened ? 'open' : 'close'} ${overlays[step.overlay].label}`);
            settle(overlays);
            const up = overlays.filter((item) => item.opened).map((item) => item.label);
            const locked = document.classes.has('modal-open');
            if (locked !== up.length > 0)
              wrong.push(
                up.length > 0
                  ? `after ${done.join(', ')}: the page scrolls behind ${up.join(' and ')}, still up`
                  : `after ${done.join(', ')}: the page stays scroll-locked with nothing up`
              );
          }
        } finally {
          overlays.forEach((item) => {
            item.opened = false;
          });
          settle(overlays);
        }
      }
    }
  } finally {
    globalThis.document = shippedDocument;
    globalThis.HTMLElement = shippedElement;
  }

  assert.ok(orders > 0, 'no order of two overlays was driven');
  assert.deepEqual(wrong, [], `${wrong.length} step(s) left the scroll lock wrong`);
});
