import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Management > Storage > Cache slice size shows an error block with Retry when the size cannot be
 * read. While the read that Retry started is still out, the Retry button shows that it is busy, so
 * a Retry that fails with the same message still shows the user that it ran.
 *
 * The shipped card and the shipped error block run here on small hook stores; every other component
 * is a stub, and the test reads the props the Retry button is drawn with, so it does not care
 * whether the card or the error block keeps the busy state.
 */

const CARD = 'src/components/features/management/cache/CacheSettings.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name `path` imports, so a stub stands in for any the test does not bind itself. */
const importedNames = (path) =>
  parseSource(path)
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

/** One component's hooks; `draw` starts a render and hands back the effects it asked for. */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const index = cursor++;
      const slot = (slots[index] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => {
      const index = cursor++;
      return (slots[index] ??= { current: initial });
    },
    useMemo: (build) => {
      const index = cursor++;
      return (slots[index] ??= { value: build() }).value;
    },
    useCallback: (callback) => {
      cursor++;
      return callback;
    },
    useEffect: (effect) => {
      cursor++;
      effects.push(effect);
    },
    useLayoutEffect: (effect) => {
      cursor++;
      effects.push(effect);
    }
  };
  const draw = () => {
    cursor = 0;
    effects = [];
    return () => effects;
  };
  return { hooks, draw };
};

const Button = stub('Button');

const turn = () => new Promise((resolve) => setImmediate(resolve));

/** Runs the card with the slice size reads answered, in order, by `reads`. */
const mountCard = (reads) => {
  const blockStore = hookStore();
  const ErrorBlock = make(ERROR_BLOCK, 'ErrorBlock', {
    ...Object.fromEntries(importedNames(ERROR_BLOCK).map((name) => [name, stub(name)])),
    React: { ...React, ...blockStore.hooks },
    ...blockStore.hooks,
    useConnectionLost: () => false,
    Button
  });

  const cardStore = hookStore();
  const Card = make(CARD, 'CacheSettings', {
    ...Object.fromEntries(importedNames(CARD).map((name) => [name, stub(name)])),
    React: { ...React, ...cardStore.hooks },
    ...cardStore.hooks,
    useTranslation: () => ({ t: (key) => key }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    useSectionExpanded: () => [true, () => undefined],
    useAccordionGroupItem: () => undefined,
    getErrorMessage: (error) => error.message,
    formatBytes: (bytes) => `${bytes} B`,
    ApiService: {
      getCacheSliceSize: () => reads.shift()(),
      setCacheSliceSize: async () => undefined
    },
    ErrorBlock
  });
  const props = { authMode: 'authenticated', mockMode: false, onError: () => undefined };

  /** The Retry buttons drawn now, with the error block drawn out. */
  const retryButtons = () => {
    const effects = cardStore.draw();
    const found = [];
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (!React.isValidElement(node)) return;
      if (node.type === ErrorBlock) {
        blockStore.draw();
        walk(ErrorBlock(node.props));
        return;
      }
      if (node.type === Button && node.props.children === 'common.retry') found.push(node.props);
      Object.values(node.props).forEach(walk);
    };
    walk(Card(props));
    return { found, effects: effects() };
  };
  return { retryButtons };
};

/** A read the test answers when it chooses. */
const heldRead = () => {
  let fail;
  const promise = new Promise((_, reject) => {
    fail = reject;
  });
  return { read: () => promise, fail };
};

test('a running Retry on the Cache slice size card shows busy until its read fails again', async () => {
  const message = 'The cache slice size could not be read';
  const retried = heldRead();
  const card = mountCard([async () => Promise.reject(new Error(message)), retried.read]);
  const originalError = console.error;
  console.error = () => undefined;
  try {
    card.retryButtons().effects.forEach((effect) => effect());
    await turn();
    await turn();

    const [failed] = card.retryButtons().found;
    assert.ok(failed, 'a failed slice size read draws no Retry button');
    assert.ok(!failed.loading, 'Retry shows busy before it was clicked');

    failed.onClick();
    await turn();
    const [running] = card.retryButtons().found;
    assert.ok(running, 'clicking Retry removes the Retry button while the read is out');
    assert.equal(
      running.loading,
      true,
      `while the read Retry started is still out, the Retry button is drawn with loading ${String(running.loading)}, so a Retry that fails with the same message changes nothing on screen`
    );

    retried.fail(new Error(message));
    await turn();
    await turn();
    const [again] = card.retryButtons().found;
    assert.ok(again, 'a Retry that failed again draws no Retry button');
    assert.ok(!again.loading, 'a Retry that failed again still shows busy');
  } finally {
    console.error = originalError;
  }
});
