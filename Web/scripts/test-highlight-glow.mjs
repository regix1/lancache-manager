import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, declaredValue, liftHookCallback } from './transpile-module.mjs';

const SOURCE = 'src/components/ui/HighlightGlow.tsx';

// The shipped effect that glides the page to the target and stops when the user takes over.
const effectSource = liftHookCallback(SOURCE, 'useEffect', 'USER_SCROLL_EVENTS');

const captureOf = (options) => options === true || options?.capture === true;

// A window that follows the DOM's listener rules: a listener is keyed by its type, function and
// capture flag, so removing it needs the same capture flag; an event stopped by a control on the page
// still runs the window's capture listeners, which run first, and never reaches its bubble listeners.
const pageWindow = () => {
  const listeners = [];
  return {
    innerHeight: 600,
    listeners,
    addEventListener(type, listener, options) {
      const capture = captureOf(options);
      if (
        !listeners.some(
          (entry) => entry.type === type && entry.listener === listener && entry.capture === capture
        )
      ) {
        listeners.push({ capture, listener, type });
      }
    },
    removeEventListener(type, listener, options) {
      const capture = captureOf(options);
      const index = listeners.findIndex(
        (entry) => entry.type === type && entry.listener === listener && entry.capture === capture
      );
      if (index >= 0) listeners.splice(index, 1);
    },
    dispatch(type, { stoppedByControl }) {
      const phases = stoppedByControl ? [true] : [true, false];
      for (const capture of phases) {
        for (const entry of listeners.filter(
          (item) => item.type === type && item.capture === capture
        )) {
          entry.listener({ type });
        }
      }
    },
    setTimeout: () => 1,
    clearTimeout: () => undefined
  };
};

const startJump = () => {
  const window = pageWindow();
  const glides = { started: 0, stopped: 0 };
  const target = {
    getBoundingClientRect: () => ({ height: 100, top: 1500 })
  };
  const effect = bindLifted(effectSource, {
    enabled: true,
    hostRef: { current: {} },
    resolveTarget: () => target,
    flashHighlight: () => undefined,
    variant: 'navigate',
    scrollIntoView: true,
    glideScroll: () => {
      glides.started += 1;
      return () => {
        glides.stopped += 1;
      };
    },
    SCROLL_SETTLE_TIMEOUT_MS: declaredValue(SOURCE, 'SCROLL_SETTLE_TIMEOUT_MS'),
    USER_SCROLL_EVENTS: declaredValue(SOURCE, 'USER_SCROLL_EVENTS'),
    window,
    document: {
      documentElement: {},
      scrollingElement: { scrollHeight: 4000, scrollTop: 0 }
    },
    ResizeObserver: class {
      observe = () => undefined;
      disconnect = () => undefined;
    },
    IntersectionObserver: class {
      observe = () => undefined;
      disconnect = () => undefined;
    }
  });
  const cleanup = effect();
  return { cleanup, glides, window };
};

for (const type of ['wheel', 'pointerdown', 'keydown']) {
  test(`a ${type} inside a control that stops the event still ends the jump`, () => {
    const { cleanup, glides, window } = startJump();
    assert.equal(glides.started, 1);

    window.dispatch(type, { stoppedByControl: true });

    assert.equal(glides.stopped, 1);
    assert.deepEqual(window.listeners, []);
    cleanup();
  });

  test(`a ${type} anywhere on the page ends the jump and leaves no listener behind`, () => {
    const { cleanup, glides, window } = startJump();

    window.dispatch(type, { stoppedByControl: false });

    assert.equal(glides.stopped, 1);
    assert.deepEqual(window.listeners, []);
    cleanup();
  });
}

test('leaving the page during the jump removes every input listener', () => {
  const { cleanup, glides, window } = startJump();
  assert.equal(window.listeners.length, 3);

  cleanup();

  assert.equal(glides.stopped, 1);
  assert.deepEqual(window.listeners, []);
});
