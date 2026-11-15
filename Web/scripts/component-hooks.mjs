/**
 * A component run by hand: its hooks keep their slots and their dependency lists between draws, and
 * each draw runs the effects whose dependencies changed, so a test drives the shipped component
 * through mount, answers and clicks and reads what it hands its children after each one.
 */

const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/** Waits one turn of the event loop, so answered promises run their continuations. */
export const turn = () => new Promise((resolve) => setImmediate(resolve));

/**
 * A fresh hook store.
 *
 * @returns {{ hooks: Record<string, Function>, draw: (body: () => unknown) => unknown }} The hooks to
 *   bind in place of React's, and a draw that runs the component body once and then its effects.
 */
export const hookStore = () => {
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

  const draw = (body) => {
    cursor = 0;
    effects = [];
    const output = body();
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
    return output;
  };
  return { hooks, draw };
};
