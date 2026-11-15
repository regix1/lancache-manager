import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { bindLifted, liftConstArrow, make } from './transpile-module.mjs';

/**
 * A full-screen setup gate hides the page under it (`aria-hidden` and `inert` on `#root`) while it
 * is drawn. Gates overlap: one fades out while the next screen's gate opens, and a modal can open on
 * the page a gate fades onto. Whichever closes first, the page must stay hidden while anything is
 * still drawn over it, and must come back, clickable and readable, once the last one has gone.
 *
 * The shipped gate runs here on a small hook store with a fake document; each gate's effect is run
 * and its cleanup kept, so the test opens and closes gates in any order. The modal stands in through
 * the shipped root and scroll holds one dialog takes while it is drawn, its closing fade included.
 * The page's scroll lock (`modal-open` on `<html>`) follows the same rule as the hidden root.
 */

/** A `#root` that records what is written to it. */
const fakeRoot = () => {
  const attributes = new Map();
  return {
    inert: false,
    setAttribute: (name, value) => attributes.set(name, String(value)),
    getAttribute: (name) => (attributes.has(name) ? attributes.get(name) : null),
    removeAttribute: (name) => attributes.delete(name),
    hidden: () => attributes.get('aria-hidden') === 'true'
  };
};

/** A document holding only `#root`, enough for the gate's effect. */
const fakeDocument = (root) => {
  const classes = new Set();
  return {
    activeElement: null,
    getElementById: (id) => (id === 'root' ? root : null),
    documentElement: {
      classList: {
        add: (name) => classes.add(name),
        remove: (name) => classes.delete(name),
        contains: (name) => classes.has(name)
      }
    },
    contains: () => false
  };
};

/** The shipped gate and the shipped root hold, both reading `document`. */
const gateWorld = () => {
  const root = fakeRoot();
  const document = fakeDocument(root);
  const holdAppRoot = bindLifted(liftConstArrow('src/utils/appRoot.ts', 'holdAppRoot'), {
    document,
    hiddenHolds: 0,
    inertHolds: 0
  });
  const holdScrollLock = bindLifted(liftConstArrow('src/utils/appRoot.ts', 'holdScrollLock'), {
    document,
    scrollHolds: 0
  });
  /** The holds one dialog takes while it is drawn; the returned function is the end of its fade. */
  const openModal = () => {
    const releaseRoot = holdAppRoot(false);
    const releaseScroll = holdScrollLock();
    return () => {
      releaseRoot();
      releaseScroll();
    };
  };
  let effects = [];
  const hooks = {
    useRef: (initial) => ({ current: initial }),
    useId: () => 'gate-title',
    useEffect: (effect) => effects.push(effect)
  };
  const SetupGate = make('src/components/modals/SetupGate.tsx', 'SetupGate', {
    ...hooks,
    React: { ...React, ...hooks },
    document,
    HTMLElement: class {},
    createPortal: (node) => node,
    Badge: () => null,
    ProgressBar: () => null,
    CustomScrollbar: () => null,
    MODAL_EXIT_MS: 250,
    useExitPresence: (open) => ({ present: open, closing: false }),
    getFocusable: () => [],
    holdAppRoot,
    holdScrollLock
  });

  /** Draws one open gate and runs its effect; the returned function is its close. */
  const openGate = () => {
    effects = [];
    SetupGate({ maxWidth: 'xl', icon: null, title: 'Sign in', opened: true, children: null });
    const cleanups = effects.map((effect) => effect());
    return () => cleanups.forEach((cleanup) => cleanup?.());
  };
  const pageState = () => ({
    hidden: root.hidden(),
    inert: root.inert,
    scrollLocked: document.documentElement.classList.contains('modal-open')
  });
  return { openGate, openModal, pageState };
};

for (const order of ['first opened closes first', 'last opened closes first']) {
  test(`two overlapping gates give the page back once both are gone (${order})`, () => {
    const { openGate, pageState } = gateWorld();
    const closeFading = openGate();
    const closeNext = openGate();
    const [closeFirst, closeSecond] =
      order === 'first opened closes first' ? [closeFading, closeNext] : [closeNext, closeFading];

    closeFirst();
    assert.deepEqual(
      pageState(),
      { hidden: true, inert: true, scrollLocked: true },
      'the page under the gate still drawn came back, or scrolls, while that gate is up'
    );
    closeSecond();
    assert.deepEqual(
      pageState(),
      { hidden: false, inert: false, scrollLocked: false },
      'the page stays hidden, unclickable or unscrollable after every gate has closed'
    );
  });
}

test('a modal opened on the page a gate fades onto keeps the page hidden until it closes', () => {
  const { openGate, openModal, pageState } = gateWorld();
  const closeGate = openGate();
  const closeModal = openModal();

  closeGate();
  assert.deepEqual(
    pageState(),
    { hidden: true, inert: false, scrollLocked: true },
    'the gate fading out took the hidden page back, or let it scroll, while a modal is still open over it'
  );
  closeModal();
  assert.deepEqual(pageState(), { hidden: false, inert: false, scrollLocked: false });
});

test('a gate opened while a modal is up leaves the page hidden for that modal when it closes', () => {
  const { openGate, openModal, pageState } = gateWorld();
  const closeModal = openModal();
  const closeGate = openGate();

  closeModal();
  assert.deepEqual(
    pageState(),
    { hidden: true, inert: true, scrollLocked: true },
    'the modal closing under an open gate gave the hidden page back, or let it scroll'
  );
  closeGate();
  assert.deepEqual(pageState(), { hidden: false, inert: false, scrollLocked: false });
});
