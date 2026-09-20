import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import { compileToUrl, MemoryStorage } from './transpile-module.mjs';

const uuidUrl = await compileToUrl('../src/utils/uuid.ts');

const ledger = await import(
  await compileToUrl(
    '../src/components/features/management/schedules/scheduled-prefill/scheduledPrefillEditSessionLedger.ts',
    { '@utils/uuid': uuidUrl }
  )
);
const { recoverScheduledPrefillEditSession } = ledger;
const storageKey = 'scheduled-prefill:edit-session:v1';
const services = ['Steam', 'Epic', 'Xbox', 'BattleNet', 'Riot'];

const createStoredLedger = (editSessionId, service, action) => ({
  version: 1,
  editSessionId,
  phase: 'active',
  cleanupId: null,
  services: Object.fromEntries(
    services.map((entry) => [
      entry,
      {
        baselineSessionId: entry === 'Steam' ? 'baseline-steam' : null,
        baselineSelectedAppIds: entry === 'Steam' ? ['10', '20'] : [],
        ...(entry === service ? action : {})
      }
    ])
  )
});

const writeLedger = (storage, value) => storage.setItem(storageKey, JSON.stringify(value));
const readLedger = (storage) => {
  const value = storage.getItem(storageKey);
  return value ? JSON.parse(value) : null;
};

const appSource = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
const recoverySource = readFileSync(
  new URL(
    '../src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillEditSessionCleanupRecovery.tsx',
    import.meta.url
  ),
  'utf8'
);
const challengeSignalRSource = readFileSync(
  new URL(
    '../src/components/features/management/schedules/scheduled-prefill/usePersistentLoginChallengeSignalR.ts',
    import.meta.url
  ),
  'utf8'
);

test('edit-session cleanup recovery is app-global and retries until confirmation', () => {
  assert.match(appSource, /<ScheduledPrefillEditSessionCleanupRecovery \/>/);
  assert.match(recoverySource, /recoverScheduledPrefillEditSession\(sessionStore/);
  assert.match(recoverySource, /cleanupPersistentPrefillEditSession/);
  assert.match(recoverySource, /request\) =>/);
  assert.match(recoverySource, /setTimeout\(\(\) =>/);
});

test('late login pushes are intent-fenced and auto-confirm remains edit-session-attributed', () => {
  assert.match(challengeSignalRSource, /hasPersistentLoginIntent\(serviceId\)/);
  assert.match(
    challengeSignalRSource,
    /\(pinnedSessionId \?\? requestedSessionId\) === eventSessionId/
  );
  assert.match(
    challengeSignalRSource,
    /providePersistentCredential\([\s\S]*?editAction\?\.editSessionId,[\s\S]*?editAction\?\.editActionId/
  );
});

test('recovery sends the exact persisted ownership and clears it after confirmation', async () => {
  const storage = new MemoryStorage();
  writeLedger(
    storage,
    createStoredLedger('edit-session-a', 'Steam', {
      selection: { editActionId: 'selection-a', sessionId: 'baseline-steam' },
      download: { editActionId: 'download-a', sessionId: 'baseline-steam' }
    })
  );
  let sent;
  await recoverScheduledPrefillEditSession(storage, async (request) => {
    sent = request;
  });
  const steam = sent.services.find((service) => service.service === 'Steam');

  assert.equal(sent.editSessionId, 'edit-session-a');
  assert.equal(steam?.prefillSessionId, 'baseline-steam');
  assert.equal(steam?.selectionSessionId, 'baseline-steam');
  assert.deepEqual(steam?.baselineSelectedAppIds, ['10', '20']);
  assert.equal(readLedger(storage), null);
});

test('recovery coalesces callers and completes a replacement ledger before resolving', async () => {
  const storage = new MemoryStorage();
  writeLedger(
    storage,
    createStoredLedger('first', 'Steam', {
      login: { editActionId: 'first-action', sessionId: 'session-a' }
    })
  );
  const sent = [];
  let release;
  const cleanup = (request) => {
    sent.push(request);
    return sent.length === 1
      ? new Promise((resolve) => {
          release = resolve;
        })
      : Promise.resolve();
  };
  const firstWait = recoverScheduledPrefillEditSession(storage, cleanup);
  const secondWait = recoverScheduledPrefillEditSession(storage, cleanup);
  assert.equal(firstWait, secondWait);
  await Promise.resolve();
  assert.equal(sent.length, 1);
  writeLedger(
    storage,
    createStoredLedger('replacement', 'Epic', {
      login: { editActionId: 'replacement-action', sessionId: 'session-b' }
    })
  );
  release();
  await firstWait;
  assert.deepEqual(
    sent.map((request) => request.editSessionId),
    ['first', 'replacement']
  );
  assert.equal(readLedger(storage), null);
});
test('failed recovery retains pending work and rejects the durable action', async () => {
  const storage = new MemoryStorage();
  writeLedger(
    storage,
    createStoredLedger('failed', 'Steam', {
      start: { editActionId: 'start', sessionId: null, returnedSessionId: null }
    })
  );
  await assert.rejects(
    recoverScheduledPrefillEditSession(storage, async () => {
      throw new Error('offline');
    }),
    /offline/
  );
  assert.equal(readLedger(storage).phase, 'cleanup-pending');
  await recoverScheduledPrefillEditSession(storage, async () => undefined);
  assert.equal(readLedger(storage), null);
});
test('no ledger makes recovery a resolved no-op', async () => {
  await recoverScheduledPrefillEditSession(new MemoryStorage(), () =>
    assert.fail('unexpected cleanup')
  );
});
