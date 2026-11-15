import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Memory diagnostics and a game mappings catalog each show an error block with Retry after a
 * failed read. Clicking Retry keeps that box on screen, with Retry busy, until the new read
 * answers: the page never goes blank and the box never vanishes only to come back when the read
 * fails again.
 *
 * The shipped components and the shipped error block run here on small hook stores; every other
 * component is a stub, and the test reads the props the Retry button is drawn with.
 */

const MEMORY = 'src/components/features/memory/MemoryDiagnostics.tsx';
const CATALOG = 'src/components/features/management/game-mappings/GameMappingsCatalog.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';
const MESSAGE = 'The read failed';

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

/**
 * One component's hooks; `draw` starts a render and hands back the effects it asked for. A memo
 * is rebuilt on every render, so a value built from state is never older than that state.
 */
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
      cursor++;
      return build();
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

/** Reads the test answers when it chooses, oldest first. */
const heldReads = () => {
  const out = [];
  const start = () =>
    new Promise((resolve, reject) => {
      out.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
    });
  /** Answers the oldest read still out. */
  const next = () => {
    const read = out.shift();
    assert.ok(read, 'no read is out');
    return read;
  };
  return { start, next };
};

/**
 * Mounts `Component` with the shipped error block; the mount effects run on the first draw only.
 * `retryButtons` draws it and returns the Retry buttons the tree holds.
 */
const mountWithErrorBlock = (path, name, bindings, props) => {
  const blockStore = hookStore();
  const ErrorBlock = make(ERROR_BLOCK, 'ErrorBlock', {
    ...Object.fromEntries(importedNames(ERROR_BLOCK).map((imported) => [imported, stub(imported)])),
    React: { ...React, ...blockStore.hooks },
    ...blockStore.hooks,
    useConnectionLost: () => false,
    Button
  });
  const store = hookStore();
  const Component = make(path, name, {
    ...Object.fromEntries(importedNames(path).map((imported) => [imported, stub(imported)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useTranslation: () => ({ t: (key) => key }),
    getErrorMessage: (error) => error.message,
    ...bindings,
    ErrorBlock
  });
  let mounted = false;
  const retryButtons = () => {
    const effects = store.draw();
    const tree = Component(props);
    if (!mounted) {
      mounted = true;
      effects().forEach((effect) => effect());
    }
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
    walk(tree);
    return found;
  };
  return { retryButtons };
};

const MEMORY_STATS = {
  totalSystemMemoryMB: 16384,
  totalSystemMemoryGB: 16,
  workingSetMB: 512,
  workingSetGB: 0.5,
  managedMB: 128,
  managedGB: 0.125,
  unmanagedMB: 384,
  unmanagedGB: 0.375,
  totalAllocatedMB: 2048,
  totalAllocatedGB: 2,
  heapSizeMB: 256,
  heapSizeGB: 0.25,
  committedMB: 300,
  committedGB: 0.29,
  fragmentedMB: 10,
  fragmentedGB: 0.01,
  threadCount: 40,
  handleCount: 900,
  gen0Collections: 10,
  gen1Collections: 5,
  gen2Collections: 1,
  timestamp: '2026-11-13T10:00:00Z'
};

/** Memory diagnostics; `reconnect` runs the read the page starts when the connection returns. */
const mountMemory = () => {
  const reads = heldReads();
  let reconnect = () => undefined;
  const view = mountWithErrorBlock(
    MEMORY,
    'MemoryDiagnostics',
    {
      useSignalR: () => ({ isConnected: true }),
      useFormattedDateTime: () => '',
      useReconnectRefetch: (_isConnected, onReconnect) => {
        reconnect = onReconnect;
      },
      ApiService: { getMemoryStats: () => reads.start() }
    },
    {}
  );
  return { ...view, reads, reconnect: () => reconnect() };
};

/** A game mappings catalog with an empty search box; its stats read always answers. */
const mountCatalog = () => {
  const reads = heldReads();
  const column = { key: 'name', headerKey: 'name', defaultWidth: 100, minWidth: 50, value: String };
  const view = mountWithErrorBlock(
    CATALOG,
    'GameMappingsCatalog',
    {
      useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
      useMockMode: () => ({ mockMode: false }),
      useAccordionGroupItem: () => undefined,
      useReconnectRefetch: () => undefined,
      themeColorVar: () => ''
    },
    {
      accordionId: 'catalog',
      accentColor: 'steam',
      columnWidthStorageKey: 'catalog-widths',
      updateEvent: 'GameMappingsUpdated',
      labels: {
        title: 'title',
        description: 'description',
        gamesInLibrary: 'gamesInLibrary',
        discovered: 'discovered',
        lastSeen: 'lastSeen',
        search: 'search',
        noGames: 'noGames',
        noResults: 'noResults'
      },
      nameColumn: column,
      identifierColumn: { ...column, key: 'id', headerKey: 'id' },
      dateColumnWidth: 120,
      loadMappings: () => reads.start(),
      loadStats: async () => ({ totalGames: 0 }),
      searchMappings: async () => [],
      loadErrorMessage: 'The catalog could not be read'
    }
  );
  return { ...view, reads };
};

/** Each box, how the test brings it on screen, and what the user sees when it goes at click. */
const SITES = {
  'Memory diagnostics before any figures were read': {
    mount: mountMemory,
    seen: 'the page goes blank',
    failFirst: async (view) => {
      view.retryButtons();
      await turn();
      view.reads.next().fail(MESSAGE);
    }
  },
  'Memory diagnostics with figures on screen': {
    mount: mountMemory,
    seen: 'the box above the figures vanishes',
    failFirst: async (view) => {
      view.retryButtons();
      await turn();
      view.reads.next().succeed(MEMORY_STATS);
      await turn();
      view.retryButtons();
      view.reconnect();
      await turn();
      view.reads.next().fail(MESSAGE);
    }
  },
  'a game mappings catalog with a short search': {
    mount: mountCatalog,
    seen: 'the box vanishes from the catalog',
    failFirst: async (view) => {
      view.retryButtons();
      await turn();
      view.reads.next().fail(MESSAGE);
    }
  }
};

for (const [site, { mount, seen, failFirst }] of Object.entries(SITES)) {
  test(`on ${site}, the error box stays drawn with Retry busy until the new read answers`, async () => {
    const originalError = console.error;
    console.error = () => undefined;
    try {
      const view = mount();
      await failFirst(view);
      await turn();
      await turn();

      const [failed] = view.retryButtons();
      assert.ok(failed, `a failed read on ${site} draws no Retry button`);
      assert.ok(!failed.loading, 'Retry shows busy before it was clicked');

      failed.onClick();
      await turn();
      const [running] = view.retryButtons();
      assert.ok(
        running,
        `clicking Retry on ${site} removes the error box while the new read is out, so ${seen} until the read fails and the box comes back`
      );
      assert.equal(
        running.loading,
        true,
        `while the read Retry started on ${site} is out, the Retry button is drawn with loading ${String(running.loading)}`
      );

      view.reads.next().fail(MESSAGE);
      await turn();
      await turn();
      const [again] = view.retryButtons();
      assert.ok(again, `a Retry that failed again on ${site} draws no Retry button`);
      assert.ok(!again.loading, `Retry on ${site} still shows busy after the read failed again`);
    } finally {
      console.error = originalError;
    }
  });
}
