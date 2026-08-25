import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

/**
 * useCacheRemovalActive() gates every Remove button in the game-cache domain
 * (Game Cache Detector, Evicted Items). It must stay true while a cache-domain
 * removal (single, evicted, or a cache bulk run) is active, and false while an
 * unrelated batch (log removal) runs - a log batch's bulk_removal card must not
 * grey out buttons that have nothing to do with it. No component render is
 * needed: the hook is a pure `useMemo` over `notifications`, so `useMemo` and
 * `useNotifications` are stubbed to plain functions and the hook is called
 * directly, the same way the other hook tests in this directory do it.
 */

const moduleUrl = (source) =>
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

// Runs the factory on every call and ignores the dependency array, so these tests
// check what the hook computes, never whether the memo is keyed on the right values.
const reactStubUrl = moduleUrl(`export const useMemo = (fn) => fn();`);

const notificationsStubUrl = moduleUrl(`
  export const box = { notifications: [] };
  export const useNotifications = () => ({ notifications: box.notifications });
`);

/** Loads the real hook, aliasing `react` and `useNotifications` to the stubs above. */
const loadUseCacheRemovalActive = async () => {
  const hookUrl = await compileToUrl('../src/hooks/useCacheRemovalActive.ts', {
    react: reactStubUrl,
    '@contexts/notifications/useNotifications': notificationsStubUrl
  });
  const { useCacheRemovalActive } = await import(hookUrl);
  return useCacheRemovalActive;
};

const activeFor = async (notifications) => {
  const { box } = await import(notificationsStubUrl);
  box.notifications = notifications;
  const useCacheRemovalActive = await loadUseCacheRemovalActive();
  return useCacheRemovalActive();
};

test('no notifications: the gate is off', async () => {
  assert.equal(await activeFor([]), false);
});

test('a running log batch does not gate the game-cache controls', async () => {
  const active = await activeFor([
    { id: 'x', type: 'bulk_removal', status: 'running', details: { itemTypes: ['log_removal'] } }
  ]);
  assert.equal(active, false, 'a log batch has nothing to do with cache removals');
});

test('a running cache batch (game and service items) gates the controls', async () => {
  const active = await activeFor([
    {
      id: 'x',
      type: 'bulk_removal',
      status: 'running',
      details: { itemTypes: ['game_removal', 'service_removal'] }
    }
  ]);
  assert.equal(active, true, 'the cache batch owns game and service removal events');
});

test('a running evicted batch gates the controls', async () => {
  const active = await activeFor([
    {
      id: 'x',
      type: 'bulk_removal',
      status: 'running',
      details: { itemTypes: ['eviction_removal'] }
    }
  ]);
  assert.equal(active, true);
});

test('a single running or queued removal still gates the controls', async () => {
  for (const type of ['game_removal', 'service_removal', 'eviction_removal']) {
    for (const status of ['running', 'waiting']) {
      const active = await activeFor([{ id: 'x', type, status, details: {} }]);
      assert.equal(active, true, `${type} ${status} should gate`);
    }
  }
});
