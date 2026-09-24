import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow } from './transpile-module.mjs';

/**
 * The memory page reads its figures on mount, again when the connection returns, and on Retry, so
 * two reads can be on the wire at once. When the server was hung rather than down, the older read
 * can fail after the newer one answered; only the newest read may write the page.
 */

test('an older memory read that fails after a newer one answered leaves the figures and no error', async () => {
  const state = { stats: [], errors: [], loading: [] };
  const reads = [];
  const fetchMemoryStats = bindLifted(
    liftConstArrow('src/components/features/memory/MemoryDiagnostics.tsx', 'fetchMemoryStats'),
    {
      statsRequestRef: { current: 0 },
      ApiService: {
        getMemoryStats: () =>
          new Promise((resolve, reject) => {
            reads.push({ resolve, reject });
          })
      },
      setStats: (value) => state.stats.push(value),
      setError: (value) => state.errors.push(value),
      setLoading: (value) => state.loading.push(value),
      getErrorMessage: (error) => error.message,
      console: { error: () => undefined }
    }
  );

  const older = fetchMemoryStats();
  const newer = fetchMemoryStats();
  reads[1].resolve({ workingSetMB: 2 });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.stats, [{ workingSetMB: 2 }]);
  assert.equal(state.errors.at(-1), null, 'the stale failure never reaches the page');
  assert.deepEqual(state.loading, [false]);
});
