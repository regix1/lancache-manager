import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Schedules history and Display preferences each show an error block with Retry after a failed
 * read. A read can already be out when the user clicks Retry: a schedule change or a reconnect
 * starts one, and the reconnect case puts the box back on screen at the moment that read starts.
 * Retry then has to show busy until the read that will replace the box settles, in either order
 * of the two steps: the other read first and then the click, or the click first and then the
 * other read.
 *
 * The shipped components, the shipped provider and the shipped error block run here on small hook
 * stores; every other component is a stub, and the test reads the props the Retry button is drawn
 * with, so it does not care whether the component or the error block keeps the busy state.
 */

const SCHEDULE_HISTORY = 'src/components/features/management/schedules/ScheduleHistory.tsx';
const SESSION_PREFERENCES = 'src/contexts/SessionPreferencesContext.tsx';
const DISPLAY_PREFERENCES = 'src/components/features/management/sections/DisplayPreferences.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';
const SESSION = 'session-1';
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

/** The shipped error block on its own hook store, and the Retry buttons a drawn tree holds. */
const errorBlockHarness = () => {
  const blockStore = hookStore();
  const ErrorBlock = make(ERROR_BLOCK, 'ErrorBlock', {
    ...Object.fromEntries(importedNames(ERROR_BLOCK).map((name) => [name, stub(name)])),
    React: { ...React, ...blockStore.hooks },
    ...blockStore.hooks,
    useConnectionLost: () => false,
    Button
  });
  const retryButtons = (tree) => {
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
  return { ErrorBlock, retryButtons };
};

/** Reads the test answers when it chooses; an aborted one rejects the way `fetch` does. */
const heldReads = () => {
  const out = [];
  const start = (signal) =>
    new Promise((_, reject) => {
      const read = { fail: (message) => reject(new Error(message)), aborted: false };
      signal?.addEventListener('abort', () => {
        read.aborted = true;
        reject(Object.assign(new Error('aborted'), { name: 'AbortError' }));
      });
      out.push(read);
    });
  /** The newest read still out. */
  const newest = () => out.filter((read) => !read.aborted).at(-1);
  return { start, newest };
};

/** Schedules history, its schedule-change broadcast and its reconnect read. */
const mountScheduleHistory = () => {
  const reads = heldReads();
  const block = errorBlockHarness();
  const store = hookStore();
  const handlers = {};
  let reconnect = () => undefined;
  const ScheduleHistory = make(SCHEDULE_HISTORY, 'ScheduleHistory', {
    ...Object.fromEntries(importedNames(SCHEDULE_HISTORY).map((name) => [name, stub(name)])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useTranslation: () => ({ t: (key) => key, i18n: { exists: () => false } }),
    useSignalR: () => ({
      on: (name, handler) => {
        handlers[name] = handler;
      },
      off: () => undefined,
      isConnected: true
    }),
    useReconnectRefetch: (_isConnected, onReconnect) => {
      reconnect = onReconnect;
    },
    ApiService: { getScheduleHistory: (_query, signal) => reads.start(signal) },
    getErrorMessage: (error) => error.message,
    isAbortError: (error) => error?.name === 'AbortError',
    formatCount: (value) => String(value),
    formatWarningCounts: () => ({}),
    rowToggleHandlers: () => ({}),
    formatServiceLabel: (key) => key,
    translateStageKeyMessage: (text) => text,
    VARIANT_BY_STATUS: {},
    SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY: {},
    scheduleTitleKey: () => null,
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY: {},
    SCHEDULE_HISTORY_PAGE_SIZE: 20,
    ErrorBlock: block.ErrorBlock
  });
  let mounted = false;
  const retryButtons = () => {
    const effects = store.draw();
    const tree = ScheduleHistory();
    if (!mounted) {
      mounted = true;
      effects().forEach((effect) => effect());
    }
    return block.retryButtons(tree);
  };
  return {
    reads,
    retryButtons,
    otherReads: {
      'a schedule change broadcast': () => handlers.SchedulesUpdated([]),
      'a reconnect': () => reconnect()
    }
  };
};

/** Display preferences under the shipped session preferences provider, and its reconnect read. */
const mountDisplayPreferences = () => {
  const reads = heldReads();
  globalThis.fetch = () => reads.start(undefined);
  const block = errorBlockHarness();
  const preferencesStore = hookStore();
  let reconnect = () => undefined;
  const SessionPreferences = make(SESSION_PREFERENCES, 'SessionPreferencesProvider', {
    ...Object.fromEntries(importedNames(SESSION_PREFERENCES).map((name) => [name, stub(name)])),
    React: { ...React, ...preferencesStore.hooks },
    ...preferencesStore.hooks,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useAuth: () => ({ isAdmin: true, hasSession: true, sessionId: SESSION, isLoading: false }),
    useReconnectRefetch: (_isConnected, onReconnect) => {
      reconnect = onReconnect;
    },
    ApiService: { getFetchOptions: () => ({}) },
    assertOk: async () => undefined,
    getErrorMessage: (error) => error.message,
    SessionPreferencesContext: { Provider: stub('SessionPreferencesContext') }
  });
  let preferences = null;
  const pageStore = hookStore();
  const DisplayPreferences = make(DISPLAY_PREFERENCES, 'DisplayPreferences', {
    ...Object.fromEntries(importedNames(DISPLAY_PREFERENCES).map((name) => [name, stub(name)])),
    React: { ...React, ...pageStore.hooks },
    ...pageStore.hooks,
    useTranslation: () => ({ t: (key) => key }),
    useSessionPreferences: () => preferences,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    APP_EVENTS: { PREFERENCE_CHANGED: 'preference-changed' },
    ErrorBlock: block.ErrorBlock
  });
  const retryButtons = () => {
    const effects = preferencesStore.draw();
    preferences = SessionPreferences({ children: null }).props.value;
    effects().forEach((effect) => effect());
    pageStore.draw();
    return block.retryButtons(DisplayPreferences());
  };
  return { reads, retryButtons, otherReads: { 'a reconnect': () => reconnect() } };
};

/** Each page, and the reads other than Retry that can already be out on it. */
const PAGES = {
  'Schedules history': {
    mount: mountScheduleHistory,
    otherReads: ['a schedule change broadcast', 'a reconnect']
  },
  'Display preferences': { mount: mountDisplayPreferences, otherReads: ['a reconnect'] }
};

for (const [page, { mount, otherReads }] of Object.entries(PAGES)) {
  for (const otherRead of otherReads) {
    for (const order of ['the other read, then the click', 'the click, then the other read']) {
      test(`on ${page}, Retry shows busy until the read settles when ${otherRead} and the click come in the order ${order}`, async () => {
        const originalError = console.error;
        const originalFetch = globalThis.fetch;
        console.error = () => undefined;
        try {
          const view = mount();
          view.retryButtons();
          await turn();
          view.reads.newest().fail(MESSAGE);
          await turn();
          await turn();

          const [failed] = view.retryButtons();
          assert.ok(failed, `a failed read on ${page} draws no Retry button`);
          assert.ok(!failed.loading, 'Retry shows busy before it was clicked');

          const steps = {
            other: () => view.otherReads[otherRead](),
            click: () => view.retryButtons()[0].onClick()
          };
          const sequence =
            order === 'the other read, then the click' ? ['other', 'click'] : ['click', 'other'];
          for (const step of sequence) {
            steps[step]();
            await turn();
          }
          await turn();

          const [running] = view.retryButtons();
          assert.ok(
            running,
            `clicking Retry on ${page} removes the Retry button while a read is out`
          );
          assert.ok(view.reads.newest(), `no read is out on ${page} after ${otherRead} and Retry`);
          assert.equal(
            running.loading,
            true,
            `on ${page}, after ${order} (${otherRead}), the Retry button is drawn with loading ${String(running.loading)} while the read that will replace the box is still out`
          );

          view.reads.newest().fail(MESSAGE);
          await turn();
          await turn();
          await turn();
          const [again] = view.retryButtons();
          assert.ok(again, `a read that failed again on ${page} draws no Retry button`);
          assert.ok(
            !again.loading,
            `Retry on ${page} still shows busy after the read failed again`
          );
        } finally {
          console.error = originalError;
          globalThis.fetch = originalFetch;
        }
      });
    }
  }
}
