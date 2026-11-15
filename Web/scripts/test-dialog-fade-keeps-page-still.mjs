import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { hookStore } from './component-hooks.mjs';
import { featureTree } from './component-tree.mjs';
import { compileTree, ordersOf } from './transpile-module.mjs';

/**
 * A dialog the user closes fades out for `MODAL_EXIT_MS` and is drawn over the page the whole time.
 * While any dialog is still drawn, open or fading, the page behind must stay scroll-locked
 * (`modal-open` on `<html>`) and hidden from screen readers (`aria-hidden` on `#root`): a wheel turn
 * or a trackpad's momentum carried over from scrolling the dialog would otherwise move the page under
 * the fading dialog. Once the last dialog is gone, the page scrolls and is read again. A second dialog that
 * opens while the first one fades keeps the page locked until it, too, has gone.
 *
 * The shipped dialog runs on one hook store per dialog, with a fake document and timers the test
 * holds, so each dialog's fade ends only when the test lets it, in every order of the two dialogs'
 * opens, closes and fade ends. The fake document and clock follow the ones in
 * `test-overlays-share-one-scroll-lock.mjs`.
 */

/** The page's `<html>` and `#root`, as far as the dialog reaches them. */
const fakeDocument = () => {
  const classes = new Set();
  const attributes = new Map();
  const root = {
    inert: false,
    attributes,
    setAttribute: (name, value) => attributes.set(name, String(value)),
    getAttribute: (name) => (attributes.has(name) ? attributes.get(name) : null),
    removeAttribute: (name) => attributes.delete(name)
  };
  return {
    classes,
    root,
    activeElement: null,
    body: {},
    getElementById: (id) => (id === 'root' ? root : null),
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

// Each dialog keeps its own hook slots; the hooks the dialog calls go to the one drawing.
let drawing = null;
const hookNames = ['useState', 'useRef', 'useMemo', 'useCallback', 'useEffect', 'useLayoutEffect'];
const hooks = {
  ...Object.fromEntries(
    hookNames.map((name) => [name, (...args) => drawing.store.hooks[name](...args)])
  ),
  useId: () => 'dialog-title'
};

/** Timers a dialog starts, each held under the dialog that started it until the test runs it. */
const held = new Map();
let nextTimer = 1;
const onHeldClock = (step) => {
  const shipped = { setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
  globalThis.setTimeout = (callback) => {
    const id = nextTimer++;
    held.set(id, { owner: drawing, callback });
    return id;
  };
  globalThis.clearTimeout = (id) => held.delete(id);
  try {
    return step();
  } finally {
    globalThis.setTimeout = shipped.setTimeout;
    globalThis.clearTimeout = shipped.clearTimeout;
  }
};

const { MODAL_EXIT_MS } = await import(await compileTree('../src/hooks/useExitPresence.ts'));
const { Root: Modal } = await featureTree('src/components/ui/Modal.tsx', 'Modal', {
  React: { ...React, ...hooks },
  ...hooks,
  createPortal: (node) => node,
  useTranslation: () => ({ t: (key) => key }),
  MODAL_EXIT_MS
});

/** One dialog on its own hook store, drawn with whatever `opened` it was last given. */
const dialog = (label) => {
  const state = { label, opened: false, shown: false, store: hookStore() };
  state.draw = () => {
    drawing = state;
    onHeldClock(() => {
      const output = state.store.draw(() =>
        Modal({ opened: state.opened, onClose: () => undefined, title: label, children: null })
      );
      state.shown = output !== null;
    });
  };
  return state;
};

/** Draws every dialog three times, so a value an effect sets is drawn and the effects it changes run. */
const redraw = (dialogs) => {
  for (let pass = 0; pass < 3; pass += 1) dialogs.forEach((item) => item.draw());
};

/** Runs the timers `owner` started, as the time they wait for passing would, then redraws. */
const runTimersOf = (owner, dialogs) => {
  for (let round = 0; round < 20; round += 1) {
    const due = [...held].filter(([, timer]) => timer.owner === owner);
    if (due.length === 0) return;
    due.forEach(([id]) => held.delete(id));
    onHeldClock(() => due.forEach(([, timer]) => timer.callback()));
    redraw(dialogs);
  }
  assert.fail(`${owner.label} kept starting timers`);
};

const STEPS = ['open', 'close', 'fade ends'];

test('the page behind a fading dialog stays scroll-locked and hidden from screen readers until the dialog is gone, in every order two dialogs open, close and finish fading', () => {
  const shippedDocument = globalThis.document;
  const shippedElement = globalThis.HTMLElement;
  globalThis.HTMLElement = class {};
  const wrong = [];
  let orders = 0;
  try {
    const steps = [0, 1].flatMap((index) => STEPS.map((kind) => ({ index, kind })));
    const valid = ordersOf(steps).filter((order) =>
      [0, 1].every((index) => {
        const at = (kind) => order.findIndex((step) => step.index === index && step.kind === kind);
        return at('open') < at('close') && at('close') < at('fade ends');
      })
    );
    for (const order of valid) {
      orders += 1;
      const document = fakeDocument();
      globalThis.document = document;
      const dialogs = [dialog('dialog A'), dialog('dialog B')];
      const done = [];
      try {
        for (const step of order) {
          const target = dialogs[step.index];
          done.push(
            step.kind === 'fade ends'
              ? `${target.label}'s fade ends`
              : `${step.kind} ${target.label}`
          );
          if (step.kind === 'fade ends') {
            runTimersOf(target, dialogs);
          } else {
            target.opened = step.kind === 'open';
            redraw(dialogs);
            // Opening also runs the short delay before the dialog starts to fade in, so only a
            // close's fade waits on the test.
            if (step.kind === 'open') runTimersOf(target, dialogs);
          }
          const shown = dialogs.filter((item) => item.shown).map((item) => item.label);
          const locked = document.classes.has('modal-open');
          const hidden = document.root.attributes.get('aria-hidden') === 'true';
          const where = `after ${done.join(', ')}`;
          if (shown.length > 0 && !locked)
            wrong.push(`${where}: the page scrolls behind ${shown.join(' and ')}, still drawn`);
          if (shown.length > 0 && !hidden)
            wrong.push(
              `${where}: screen readers reach the page behind ${shown.join(' and ')}, still drawn`
            );
          if (shown.length === 0 && locked)
            wrong.push(`${where}: the page stays scroll-locked with no dialog drawn`);
          if (shown.length === 0 && hidden)
            wrong.push(`${where}: the page stays hidden from screen readers with no dialog drawn`);
        }
      } finally {
        dialogs.forEach((item) => {
          item.opened = false;
        });
        redraw(dialogs);
        dialogs.forEach((item) => runTimersOf(item, dialogs));
        held.clear();
      }
    }
  } finally {
    globalThis.document = shippedDocument;
    globalThis.HTMLElement = shippedElement;
  }

  assert.ok(orders > 0, 'no order of two dialogs was driven');
  assert.deepEqual(wrong, [], `${wrong.length} step(s) left the page behind wrong`);
});
