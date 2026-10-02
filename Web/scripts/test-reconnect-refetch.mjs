import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  bulkRemovalCard,
  compileToUrl,
  findSoleNode,
  loadNotificationModules,
  MemoryStorage,
  moduleUrl,
  operationRunRow,
  parseSource,
  pushRun
} from './transpile-module.mjs';

/**
 * A snapshot fetched before the SignalR subscription was live misses every event raised in the
 * window between the two, and nothing replays them. The hook is the shared answer to that, so it
 * is run here for real - compiled and driven through a sequence of renders - rather than described,
 * and the two properties that pull against each other are asserted together: it must fire on a
 * connection that arrived after the caller's mount fetch, and it must stay silent when the caller
 * mounted into a connection that was already live and whose mount fetch therefore missed nothing.
 */

const hookSource = readFileSync(
  new URL('../src/hooks/useReconnectRefetch.ts', import.meta.url),
  'utf8'
);

/**
 * The smallest React that can run one hook: ordered slots for `useRef`, dependency comparison and
 * a post-render flush for `useEffect`. A data-URL import resolves no bare specifier, so the hook is
 * compiled against this module in place of the real one.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];

export const useRef = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { current: initial };
  }
  return slots[cursor++];
};

export const useEffect = (run, deps) => {
  if (!slots[cursor]) {
    slots[cursor] = { deps: null, ran: false };
  }
  const slot = slots[cursor++];
  const changed = !slot.ran || deps.some((value, index) => !Object.is(value, slot.deps[index]));
  slot.ran = true;
  slot.deps = deps;
  if (changed) {
    queued.push(run);
  }
};

export const createComponent = () => {
  const componentSlots = [];
  return {
    render(body) {
      slots = componentSlots;
      cursor = 0;
      queued = [];
      body();
      const effects = queued;
      queued = [];
      slots = null;
      effects.forEach((run) => run());
    }
  };
};
`;

const reactStubUrl = `data:text/javascript;base64,${Buffer.from(reactStubSource).toString('base64')}`;
const { createComponent } = await import(reactStubUrl);
const reconnectRefetchUrl = await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
  react: reactStubUrl
});
const { useReconnectRefetch } = await import(reconnectRefetchUrl);

// `useRepairEnd` runs over the real hook above and the runs the real store derives. The store keeps
// the repairing cards hidden in this tab in sessionStorage and decides once, when it loads, whether
// that storage works, so the storage is installed first.
globalThis.sessionStorage = new MemoryStorage();
const notificationModules = await loadNotificationModules();
const notificationsStubUrl = moduleUrl(`
  export const box = { runs: [] };
  export const useNotifications = () => ({ repairingRuns: box.runs });
`);
const { box } = await import(notificationsStubUrl);
const { useRepairEnd } = await import(
  await compileToUrl('../src/hooks/useRepairEnd.ts', {
    './useReconnectRefetch': reconnectRefetchUrl,
    '@contexts/notifications/useNotifications': notificationsStubUrl
  })
);

/**
 * Mounts one caller at the given connection state and hands back a `render` the test drives the
 * connection with. Every render passes a freshly built callback, the way a caller that closes over
 * page or filter state does.
 */
const mountCaller = (connectedAtMount) => {
  const refetches = [];
  const component = createComponent();
  const render = (isConnected) => {
    component.render(() => useReconnectRefetch(isConnected, () => refetches.push(isConnected)));
  };
  render(connectedAtMount);
  return { refetches, render };
};

test('a connection that comes up after the caller mounted refetches', () => {
  const { refetches, render } = mountCaller(false);
  assert.equal(refetches.length, 0, 'nothing to refetch while the socket is still down');

  render(true);

  assert.equal(
    refetches.length,
    1,
    'the mount fetch was taken before the subscription existed, so it has to be replaced'
  );
});

test('a connection already live at mount does not refetch', () => {
  const { refetches, render } = mountCaller(true);
  render(true);

  assert.deepEqual(
    refetches,
    [],
    'the mount fetch ran with the subscription live, so a second request would be a duplicate'
  );
});

test('a caller that mounted into a live connection still refetches after a drop', () => {
  const { refetches, render } = mountCaller(true);

  render(false);
  render(true);
  assert.equal(refetches.length, 1);

  render(false);
  render(true);
  assert.equal(refetches.length, 2, 'every recovery refetches, not just the first');
});

test('a caller that mounted disconnected refetches on the first connect and on each recovery', () => {
  const { refetches, render } = mountCaller(false);

  render(true);
  render(false);
  render(true);

  assert.equal(refetches.length, 2);
});

test('re-rendering with a new callback and an unchanged connection does not refetch', () => {
  const { refetches, render } = mountCaller(false);
  render(true);

  render(true);
  render(true);

  assert.equal(
    refetches.length,
    1,
    'a busy/page/filter change rebuilds the callback and must not fire a request'
  );
});

test('the effect watches the connection alone', () => {
  assert.match(
    hookSource,
    /\}, \[isConnected\]\);/,
    'adding the callback to the dependency list turns every unrelated re-render into a request'
  );
});

/**
 * A page watching game removal repairs over a real run store, whose one game removal is folded
 * under a bulk card and so draws no card of its own. `push` applies that run's next row; `render`
 * renders the page over the runs the store derives, with a fresh callback each time as a page does.
 */
const gameRemovalPage = () => {
  const localCards = [bulkRemovalCard({ currentOperationId: 'I1', itemOperationIds: ['I1'] })];
  let state = notificationModules.createRunStoreState();
  const reloads = [];
  const component = createComponent();
  const push = (fields) => {
    const row = operationRunRow('I1', {
      operationType: 'gameRemoval',
      name: 'Game Removal',
      ...fields
    });
    state = pushRun(notificationModules, state, row, localCards);
  };
  const render = () => {
    box.runs = notificationModules.deriveRepairingRuns(state);
    component.render(() => useRepairEnd(['game_removal'], () => reloads.push(true)));
  };
  return { push, render, reloads };
};

const repairing = { status: 'cancelled', repairing: true, repairError: null };
const repairEnded = { status: 'cancelled', repairing: false, repairError: null };

test('a repair that ends reloads the page once, for a run folded under a bulk card too', () => {
  const page = gameRemovalPage();
  page.render();
  assert.equal(page.reloads.length, 0, 'nothing repairs at mount');

  page.push({});
  page.push(repairing);
  page.render();
  assert.deepEqual(
    box.runs.map((run) => `${run.id}:${run.type}`),
    ['I1:game_removal'],
    'the folded run draws no card, and its repair still changes the page'
  );
  assert.equal(page.reloads.length, 0, 'no reload while the repair runs');

  page.push(repairEnded);
  page.render();
  assert.equal(page.reloads.length, 1);

  page.render();
  assert.equal(page.reloads.length, 1, 'a later render with nothing repairing does not reload');
});

test('a page opened while a repair runs reloads when it ends', () => {
  const page = gameRemovalPage();
  page.push({});
  page.push(repairing);
  page.render();
  assert.equal(page.reloads.length, 0);

  page.push(repairEnded);
  page.render();
  assert.equal(page.reloads.length, 1, 'what the page loaded at mount predates the repair');
});

test("pages that show eviction state reload when an eviction or corruption removal's repair ends", () => {
  const repairTypes = (relativePath) => {
    const sourceFile = parseSource(relativePath, ts.ScriptKind.TSX);
    const call = findSoleNode(
      sourceFile,
      'useRepairEnd call',
      (node) => ts.isCallExpression(node) && node.expression.getText(sourceFile) === 'useRepairEnd'
    );
    return call.arguments[0].elements.map((element) => element.text);
  };

  assert.ok(
    repairTypes('src/components/features/management/sections/StorageSection.tsx').includes(
      'eviction_removal'
    ),
    "a cancelled eviction removal's repair deletes the evicted rows the Evicted Items card lists"
  );
  assert.ok(
    repairTypes('src/components/features/management/game-detection/GameCacheDetector.tsx').includes(
      'corruption_removal'
    ),
    "a corruption removal's repair marks games evicted, so the Games list has to drop them"
  );
});
