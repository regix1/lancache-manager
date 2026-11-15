import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { compileToUrl, make, parseSource } from './transpile-module.mjs';

/**
 * A Retry in an error box stays busy until the read it stands for has answered. Three boxes start
 * that read in a way the box cannot see:
 * - Session preferences: Retry clicked while a switch's save is still being written waits for the
 *   save, and the read runs once the save ends.
 * - Excluded clients and orphaned downloads: both reload on `DownloadsRefresh`, which the server sends
 *   after every log-ingest run that saved rows. A reload that starts while Retry's read is out
 *   replaces that read, so the box keeps waiting on the newer one.
 * - The nickname list (`useLoadLifecycle`): a nickname change raised by the server forces a new load
 *   that replaces the one Retry started or joined, so Retry keeps waiting on the newer load. A
 *   sign-out while Retry waits drops the load, and Retry then stops waiting rather than staying busy.
 *
 * Each shipped component is drawn whole on a small hook store that keeps effect and callback
 * dependencies, every read held until the test answers it, and the shipped error block drawn from its
 * own source with state that lasts as long as its box is drawn, so the busy state is the one its
 * Retry button shows. Imports the test does not bind stand in as inert values.
 */

const SESSION_PREFERENCES = 'src/components/features/user/SessionPreferences.tsx';
const CLIENT_GROUPS = 'src/contexts/ClientGroupContext.tsx';
const LOAD_LIFECYCLE = 'src/hooks/useLoadLifecycle.ts';
const CLIENTS = 'src/components/features/management/sections/ClientsSection.tsx';
const STORAGE = 'src/components/features/management/sections/StorageSection.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';

const { DEFAULT_PREFERENCES } = await import(await compileToUrl('../src/types/userPreferences.ts'));

/** Every name `path` imports. */
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

/**
 * An inert stand-in for an import the test does not bind: callable, any field of it is another
 * stand-in kept for the next read, it lists nothing and it is not a promise, so a component that
 * reads it draws without a crash and without sending anything.
 */
const inert = () => {
  const fields = new Map();
  let result;
  return new Proxy(() => undefined, {
    get: (_target, key) => {
      if (key === Symbol.iterator) return () => [][Symbol.iterator]();
      if (key === Symbol.toPrimitive) return () => '';
      if (key === 'length') return 0;
      if (key === 'then' || key === '$$typeof') return undefined;
      if (!fields.has(key)) fields.set(key, inert());
      return fields.get(key);
    },
    apply: () => (result ??= inert()),
    construct: () => inert()
  });
};

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

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
    },
    useId: () => {
      cursor++;
      return 'id';
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

const Button = stub('Button');
const ErrorBlockElement = stub('ErrorBlock');

/** Reads the test answers when it chooses, oldest first. */
const heldReads = () => {
  const out = [];
  const start = () =>
    new Promise((resolve, reject) => {
      out.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
    });
  return { start, out };
};

/** Every element in a drawn tree, children and element-valued props included. */
const elementsOf = (tree) => {
  const found = [];
  const seen = new Set();
  const walk = (node) => {
    if (node === null || typeof node !== 'object' || seen.has(node)) return;
    seen.add(node);
    if (Array.isArray(node)) {
      node.forEach(walk);
      return;
    }
    if (!React.isValidElement(node)) return;
    found.push(node);
    Object.values(node.props).forEach(walk);
  };
  walk(tree);
  return found;
};

/**
 * Mounts `name` from `path` with the test's live connection, held debounce and bindings.
 *
 * @returns {{ tree: () => unknown, retry: () => object | null, deliver: (eventName: string) => void,
 *   fireDebounced: () => void, settle: () => Promise<void> }} The component drawn now; the Retry the
 *   error box titled `boxTitle` draws now, or null when no such box is drawn; a server event
 *   delivered to the component; the debounced reloads it scheduled, run now; a wait for pending work.
 */
const mountWhole = (path, name, boxTitle, props, bindings) => {
  const store = hookStore();
  const handlers = new Map();
  const debounced = [];
  const signalR = {
    on: (eventName, handler) => {
      handlers.set(eventName, [...(handlers.get(eventName) ?? []), handler]);
    },
    off: (eventName, handler) => {
      handlers.set(
        eventName,
        (handlers.get(eventName) ?? []).filter((registered) => registered !== handler)
      );
    },
    isConnected: true
  };
  const scheduleDebounced = (callback) => {
    debounced.push(callback);
  };
  const translation = { t: (key) => key, i18n: { exists: () => false, language: 'en' } };
  const Component = make(path, name, {
    ...Object.fromEntries(importedNames(path).map((imported) => [imported, inert()])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useTranslation: () => translation,
    useSignalR: () => signalR,
    useTimeoutCallback: () => scheduleDebounced,
    useReconnectRefetch: () => undefined,
    getErrorMessage: (error) => error.message,
    isAbortError: (error) => error?.name === 'AbortError',
    ErrorBlock: ErrorBlockElement,
    Button,
    ...(typeof bindings === 'function' ? bindings(store.hooks) : bindings)
  });

  // The error block drawn in the box, with its own state while the box stays drawn.
  let block = null;
  const tree = () => store.render(() => Component(props));
  const retry = () => {
    const box = elementsOf(tree()).find(
      (element) => element.type === ErrorBlockElement && element.props.title === boxTitle
    );
    if (!box) {
      block = null;
      return null;
    }
    if (!block) {
      const blockStore = hookStore();
      block = {
        blockStore,
        component: make(ERROR_BLOCK, 'ErrorBlock', {
          ...Object.fromEntries(
            importedNames(ERROR_BLOCK).map((imported) => [imported, stub(imported)])
          ),
          React: { ...React, ...blockStore.hooks },
          ...blockStore.hooks,
          useConnectionLost: () => false,
          Button
        })
      };
    }
    const drawn = block.blockStore.render(() => block.component(box.props));
    return (
      elementsOf(drawn).find(
        (element) => element.type === Button && element.props.children === 'common.retry'
      )?.props ?? null
    );
  };
  const settle = async () => {
    for (let step = 0; step < 8; step += 1) {
      await turn();
      retry();
    }
  };
  return {
    tree,
    retry,
    settle,
    deliver: (eventName) => (handlers.get(eventName) ?? []).forEach((handler) => handler({})),
    fireDebounced: () => debounced.splice(0).forEach((callback) => callback())
  };
};

/** Clicks a drawn Retry the way a browser does: a busy button is disabled, so it takes no click. */
const click = (retry) => {
  if (!retry.loading) retry.onClick();
};

/** Every other service call of a component: held forever, so nothing else it starts answers. */
const quietApi = (overrides) =>
  new Proxy(overrides, {
    get: (target, key) => (key in target ? target[key] : () => new Promise(() => undefined))
  });

const quietConsole = async (run) => {
  const consoleError = console.error;
  const consoleWarn = console.warn;
  console.error = () => undefined;
  console.warn = () => undefined;
  try {
    await run();
  } finally {
    console.error = consoleError;
    console.warn = consoleWarn;
  }
};

test('session preferences: a Retry clicked while a switch is saving stays busy until its read answers', async () => {
  await quietConsole(async () => {
    const reads = heldReads();
    const writes = heldReads();
    const pane = mountWhole(
      SESSION_PREFERENCES,
      'SessionPreferences',
      'user.panes.prefs.loadFailed',
      {
        session: { id: 'session-1', sessionType: 'user', isCurrentSession: false },
        availableThemes: [],
        defaultGuestTheme: 'dark-default',
        defaultGuestRefreshRate: 'STANDARD',
        guestDefaultsLoaded: true
      },
      {
        DEFAULT_PREFERENCES,
        PREFILL_SERVICES: [],
        prefillServiceRecord: () => ({}),
        refreshRateOptions: [],
        useErrorHandler: () => ({ notifyError: () => undefined }),
        useDefaultGuestPreferences: () => ({ prefs: { allowedTimeFormats: [] } }),
        ApiService: quietApi({
          getSessionPreferences: reads.start,
          saveSessionPreferences: writes.start,
          setSessionRefreshRate: writes.start
        })
      }
    );

    // The rows load, then a reload the server pushed fails, so the error box shows above them.
    await pane.settle();
    reads.out.shift().succeed({ ...DEFAULT_PREFERENCES });
    await pane.settle();
    pane.deliver('UserPreferencesReset');
    await pane.settle();
    reads.out.shift().fail('The read failed');
    await pane.settle();
    assert.ok(pane.retry(), 'the failed reload draws no Retry');

    // A switch is flipped and saved; its save reads the stored row, then its write is held.
    const sharpCorners = elementsOf(pane.tree()).find(
      (element) =>
        typeof element.props.onChange === 'function' &&
        String(element.props.onChange).includes("'sharpCorners'")
    );
    assert.ok(sharpCorners, 'the pane draws no sharp corners switch');
    sharpCorners.props.onChange('on');
    await pane.settle();
    const save = elementsOf(pane.tree()).find(
      (element) => element.type === Button && element.props.children === 'common.save'
    );
    assert.ok(save, 'the pane draws no Save button');
    save.props.onClick();
    await pane.settle();
    reads.out.shift().succeed({ ...DEFAULT_PREFERENCES });
    await pane.settle();
    assert.equal(writes.out.length, 1, 'the switch save is not being written');

    // Retry while the save is still being written.
    click(pane.retry());
    await pane.settle();
    const whileSaving = pane.retry();
    assert.ok(whileSaving, 'the error box went away before its read answered');
    assert.equal(
      whileSaving.loading,
      true,
      `Retry clicked while a switch was saving is drawn with loading ${String(whileSaving.loading)}, though the read it stands for has not run yet`
    );
  });
});

const NEWER_FIRST = 'the DownloadsRefresh read answers first, then the Retry read';
const RETRY_FIRST = 'the Retry read answers first, then the DownloadsRefresh read';

/**
 * Fails the first read, clicks Retry, lets `DownloadsRefresh` start a reload during it, and answers
 * the two reads in the order `order` ends with. Returns what was wrong with the Retry on the way.
 */
const retryDuringDownloadsRefresh = async (mount, reads, answer, order) => {
  const section = mount();
  await section.settle();
  reads.out.shift().fail('The read failed');
  await section.settle();
  const failed = section.retry();
  assert.ok(failed, `${order}: the failed read draws no Retry`);

  click(failed);
  await section.settle();
  section.deliver('DownloadsRefresh');
  section.fireDebounced();
  await section.settle();
  assert.equal(
    reads.out.length,
    2,
    `${order}: Retry and DownloadsRefresh did not leave two reads out`
  );
  const [retryRead, refreshRead] = reads.out.splice(0, 2);

  const wrong = [];
  const retryAnswersFirst = order.endsWith(RETRY_FIRST);
  const first = retryAnswersFirst ? retryRead : refreshRead;
  const second = retryAnswersFirst ? refreshRead : retryRead;
  first.succeed(answer);
  await section.settle();
  const between = section.retry();
  if (between && !between.loading)
    wrong.push(`${order}: with one read still out, Retry is drawn as not busy`);
  second.succeed(answer);
  await section.settle();
  return wrong;
};

test('excluded clients and orphaned downloads: Retry stays busy when DownloadsRefresh lands during its read', async () => {
  await quietConsole(async () => {
    const wrong = [];
    for (const order of [RETRY_FIRST, NEWER_FIRST]) {
      const exclusionReads = heldReads();
      wrong.push(
        ...(await retryDuringDownloadsRefresh(
          () =>
            mountWhole(
              CLIENTS,
              'ClientsSection',
              'management.sections.clients.errors.failedToLoadExcluded',
              {
                isAdmin: true,
                authMode: 'authenticated',
                mockMode: false,
                onError: () => undefined,
                onSuccess: () => undefined
              },
              {
                ApiService: quietApi({ getStatsExclusions: exclusionReads.start })
              }
            ),
          exclusionReads,
          { rules: [] },
          `excluded clients, ${order}`
        ))
      );

      const orphanedReads = heldReads();
      wrong.push(
        ...(await retryDuringDownloadsRefresh(
          () =>
            mountWhole(
              STORAGE,
              'StorageSectionContent',
              'management.sections.data.orphanedDownloadsLoadError',
              {
                isAdmin: true,
                authMode: 'authenticated',
                mockMode: false,
                gameCacheRefreshKey: 0,
                onError: () => undefined,
                onSuccess: () => undefined,
                onDataRefresh: () => undefined
              },
              {
                ApiService: quietApi({ getOrphanedDownloads: orphanedReads.start }),
                useSectionExpanded: (_key, initial) => [initial, () => undefined]
              }
            ),
          orphanedReads,
          { groups: [] },
          `orphaned downloads, ${order}`
        ))
      );
    }

    assert.deepEqual(
      wrong,
      [],
      `a reload DownloadsRefresh started ended the Retry's busy state early: ${wrong.join('; ')}`
    );
  });
});

const RETRY_STARTS_THE_LOAD = 'Retry starts the load, then a nickname change forces another';
const RETRY_JOINS_THE_LOAD =
  'a nickname change forces a load, Retry joins it, then a second change forces another';
const SIGN_OUT_WHILE_WAITING = 'Retry joins a forced load, then the viewer signs out';

/** The nickname list for a signed-in viewer, its reads held until answered and dropped on abort. */
const mountNicknames = () => {
  const reads = [];
  const auth = { authMode: 'authenticated', isLoading: false };
  const managerLoading = {
    isLoading: false,
    beginLoad: () => undefined,
    markLoaded: () => undefined,
    markFailed: () => undefined
  };
  const heldGroupsRead = (signal) =>
    new Promise((resolve, reject) => {
      const read = { succeed: resolve, fail: (message) => reject(new Error(message)) };
      reads.push(read);
      signal?.addEventListener('abort', () => {
        reads.splice(reads.indexOf(read), 1);
        reject(Object.assign(new Error('aborted'), { name: 'AbortError' }));
      });
    });
  const bindings = (hooks) => ({
    useAuth: () => auth,
    useMockMode: () => ({ mockMode: false }),
    useManagerLoading: () => managerLoading,
    ClientGroupContext: { Provider: stub('ClientGroupContext') },
    useLoadLifecycle: make(LOAD_LIFECYCLE, 'useLoadLifecycle', {
      ...hooks,
      getErrorMessage: (error) => error.message,
      isAbortError: (error) => error?.name === 'AbortError'
    }),
    ApiService: quietApi({ getClientGroups: heldGroupsRead })
  });
  const list = mountWhole(CLIENT_GROUPS, 'ClientGroupProvider', null, {}, bindings);
  return { list, reads, auth, value: () => list.tree().props.value };
};

/** Clicks the nickname list's Retry; the returned flag reads whether its busy state has ended. */
const clickNicknameRetry = (nicknames) => {
  let ended = false;
  void nicknames
    .value()
    .refreshGroups()
    .finally(() => {
      ended = true;
    });
  return () => ended;
};

test('nickname list: a Retry stays busy until the newest load ends, and a sign-out ends its wait', async () => {
  await quietConsole(async () => {
    const wrong = [];
    for (const order of [RETRY_STARTS_THE_LOAD, RETRY_JOINS_THE_LOAD, SIGN_OUT_WHILE_WAITING]) {
      const nicknames = mountNicknames();
      await nicknames.list.settle();
      nicknames.reads.shift().fail('The read failed');
      await nicknames.list.settle();
      assert.equal(
        nicknames.value().error,
        'The read failed',
        `${order}: the failed read shows no error`
      );

      const forceLoad = async () => {
        nicknames.list.deliver('ClientGroupUpdated');
        nicknames.list.fireDebounced();
        await nicknames.list.settle();
      };
      let retryEnded;
      if (order === RETRY_STARTS_THE_LOAD) {
        retryEnded = clickNicknameRetry(nicknames);
        await nicknames.list.settle();
        await forceLoad();
      } else {
        await forceLoad();
        retryEnded = clickNicknameRetry(nicknames);
        await nicknames.list.settle();
        if (order === RETRY_JOINS_THE_LOAD) await forceLoad();
      }

      if (order === SIGN_OUT_WHILE_WAITING) {
        nicknames.auth.authMode = 'unauthenticated';
        await nicknames.list.settle();
        if (!retryEnded())
          wrong.push(`${order}: Retry is still busy after the sign-out dropped its load`);
        continue;
      }

      assert.equal(nicknames.reads.length, 1, `${order}: more than the newest load is still out`);
      if (retryEnded())
        wrong.push(`${order}: Retry stopped being busy while the newest load is out`);
      nicknames.reads.shift().succeed([]);
      await nicknames.list.settle();
      if (!retryEnded()) wrong.push(`${order}: Retry is still busy after the newest load ended`);
    }

    assert.deepEqual(
      wrong,
      [],
      `the nickname list's Retry does not follow the newest load: ${wrong.join('; ')}`
    );
  });
});
