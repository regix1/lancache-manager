import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * The failed games of a scheduled prefill run load when the run's details open, and a failed read
 * shows an error box with Retry. The details region unmounts what it holds when it closes
 * (`CollapsibleRegion.tsx:103-105`), so collapsing and reopening the row while a Retry read is out
 * draws a new box. That box's Retry must still show busy and take no click while the read is out,
 * or a second Retry starts a second read whose answer can land over the first one's. The one read
 * still out then decides what the row shows: the list it loaded.
 *
 * The shipped row and the shipped error block run here on small hook stores with stubs at their
 * edges, the way `test-retry-sites-keep-error-box.mjs` drives the same row (its harness is private to
 * it). The details region is drawn the way it behaves: its children while open, nothing once closed,
 * so the error block's own state starts again when the row reopens.
 */

const RECENT_RUN =
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillActivityModal.tsx';
const ERROR_BLOCK = 'src/components/ui/ErrorBlock.tsx';
const LOAD_FAILED_TITLE =
  'management.schedules.services.scheduledPrefill.config.activity.failedGamesLoadFailed';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name `path` imports, so a stub stands in for any the test does not bind itself. */
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

/** One mounted component's hooks; `draw` starts a render. */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
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
    useEffect: () => {
      cursor++;
    },
    useLayoutEffect: () => {
      cursor++;
    },
    useMemo: (build) => {
      cursor++;
      return build();
    },
    useCallback: (callback) => {
      cursor++;
      return callback;
    },
    useId: () => {
      cursor++;
      return 'details';
    }
  };
  return {
    hooks,
    draw: () => {
      cursor = 0;
    }
  };
};

const turn = () => new Promise((resolve) => setImmediate(resolve));

const settle = async () => {
  for (let step = 0; step < 4; step += 1) await turn();
};

const Button = stub('Button');
const CollapsibleRegion = stub('CollapsibleRegion');

const FAILED_RUN = {
  runId: 'run-1',
  scheduleName: 'Nightly',
  completedAtUtc: '2026-11-13T11:00:00Z',
  historyIncomplete: false,
  options: { selection: 'all', operatingSystems: [] },
  snapshot: {
    state: 'failed',
    reason: null,
    failedApps: 1,
    completedApps: 1,
    cachedApps: 0,
    totalApps: 2,
    bytesTransferred: 0,
    startedAt: '2026-11-13T10:00:00Z'
  }
};

const LOADED_GAMES = [{ appId: 440, name: 'Team Fortress 2', reasonKey: 'reasons.noDepots' }];

/** The row with reads the test answers when it chooses, oldest first. */
const mountRow = () => {
  const reads = [];
  const rowStore = hookStore();
  const Row = make(RECENT_RUN, 'ScheduledPrefillRecentRun', {
    ...Object.fromEntries(importedNames(RECENT_RUN).map((name) => [name, stub(name)])),
    React: { ...React, ...rowStore.hooks },
    ...rowStore.hooks,
    useTranslation: () => ({ t: (key) => key }),
    SCHEDULED_PREFILL_PLATFORM_UI: { steam: { icon: stub('SteamIcon'), rowClassName: 'row' } },
    getPersistentServiceId: () => 'steam',
    getPrefillRunReasonKey: () => null,
    formatBytes: (bytes) => `${bytes} B`,
    formatCount: (value) => String(value),
    formatDurationFromSeconds: (seconds) => `${seconds} s`,
    getErrorMessage: (error) => error.message,
    ApiError: class ApiError extends Error {},
    ApiService: {
      getPersistentPrefillRunFailedGames: () =>
        new Promise((resolve, reject) => {
          reads.push({ succeed: resolve, fail: (message) => reject(new Error(message)) });
        })
    },
    CollapsibleRegion,
    Button
  });

  // The error block mounted inside the open region, or null while none is drawn there.
  let block = null;
  const mountBlock = () => {
    const store = hookStore();
    const component = make(ERROR_BLOCK, 'ErrorBlock', {
      ...Object.fromEntries(importedNames(ERROR_BLOCK).map((name) => [name, stub(name)])),
      React: { ...React, ...store.hooks },
      ...store.hooks,
      useConnectionLost: () => false,
      Button
    });
    return { store, component };
  };

  /** What the row shows now: its toggle, the error box's Retry, and the game names listed. */
  const view = () => {
    rowStore.draw();
    const tree = Row({ run: FAILED_RUN, serviceKey: 'steam' });
    const seen = { toggle: null, errorBoxes: 0, retry: null, listed: [] };
    let blockDrawn = false;
    const walk = (node) => {
      if (Array.isArray(node)) {
        node.forEach(walk);
        return;
      }
      if (typeof node === 'string') {
        if (LOADED_GAMES.some((game) => game.name === node)) seen.listed.push(node);
        return;
      }
      if (!React.isValidElement(node)) return;
      if (node.type === CollapsibleRegion) {
        if (node.props.open) walk(node.props.children);
        return;
      }
      if (node.type?.name === 'ErrorBlock' || node.props.title === LOAD_FAILED_TITLE) {
        seen.errorBoxes += 1;
        blockDrawn = true;
        block ??= mountBlock();
        block.store.draw();
        const drawn = block.component(node.props);
        const visit = (inner) => {
          if (Array.isArray(inner)) return inner.forEach(visit);
          if (!React.isValidElement(inner)) return;
          if (inner.type === Button && inner.props.children === 'common.retry')
            seen.retry = inner.props;
          Object.values(inner.props).forEach(visit);
        };
        visit(drawn);
        return;
      }
      if (node.type === Button && 'aria-expanded' in node.props) seen.toggle = node.props;
      Object.values(node.props).forEach(walk);
    };
    walk(tree);
    if (!blockDrawn) block = null;
    return seen;
  };

  return { view, reads };
};

/** Clicks a drawn Retry the way a browser does: a busy button is disabled, so it takes no click. */
const click = (retry) => {
  if (!retry.loading) retry.onClick();
};

test('a Retry read of the failed games still out after collapse and reopen keeps Retry busy and decides the list', async () => {
  const consoleError = console.error;
  console.error = () => undefined;
  const wrong = [];
  try {
    const row = mountRow();

    // The details open, the first read fails, and the error box offers Retry.
    row.view().toggle.onClick();
    await settle();
    row.reads.shift().fail('The read failed');
    await settle();
    const failed = row.view();
    assert.ok(failed.retry, 'a failed read draws no Retry');

    // Retry, then collapse and reopen the row while that read is out.
    click(failed.retry);
    await settle();
    assert.equal(row.reads.length, 1, 'Retry did not start a read');
    row.view().toggle.onClick();
    row.view();
    row.view().toggle.onClick();
    await settle();
    const reopened = row.view();
    assert.ok(reopened.retry, 'the reopened row draws no Retry');
    if (reopened.retry.loading !== true)
      wrong.push(
        `the reopened Retry is drawn with loading ${String(reopened.retry.loading)} while its read is out`
      );

    // A click on the reopened Retry; a busy button takes none.
    click(reopened.retry);
    await settle();
    if (row.reads.length !== 1)
      wrong.push(`a click on the reopened Retry left ${row.reads.length} reads out instead of 1`);

    // The read still out answers with the list.
    row.reads.shift().succeed(LOADED_GAMES);
    await settle();
    const shown = row.view();
    if (shown.errorBoxes > 0 || shown.listed.length !== LOADED_GAMES.length)
      wrong.push(
        `once the read answered, the row shows ${shown.errorBoxes} error box(es) and lists ${shown.listed.length} of the ${LOADED_GAMES.length} game(s) it loaded`
      );
  } finally {
    console.error = consoleError;
  }

  assert.deepEqual(
    wrong,
    [],
    `after a collapse and reopen during a Retry read of the failed games: ${wrong.join('; ')}`
  );
});
