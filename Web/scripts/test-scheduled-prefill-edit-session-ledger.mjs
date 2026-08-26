import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import {
  beginEditSessionCleanup,
  buildEditSessionCleanupRequest,
  clearConfirmedEditSession,
  createScheduledPrefillEditSession,
  discardCommittedEditSession,
  loadScheduledPrefillEditSession,
  recordEditActionIntent,
  recordEditSessionStartResult
} from '../src/components/features/management/schedules/scheduled-prefill/scheduledPrefillEditSessionLedger.ts';
import { MemoryStorage } from './transpile-module.mjs';

const baseline = {
  selectedAppIdsByService: {
    Steam: ['10', '20'],
    Epic: [],
    Xbox: [],
    BattleNet: [],
    Riot: []
  },
  sessionIdByService: {
    Steam: 'baseline-steam',
    Epic: null,
    Xbox: null,
    BattleNet: null,
    Riot: null
  }
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

test('an untouched edit session never becomes durable cleanup work', () => {
  const storage = new MemoryStorage();
  createScheduledPrefillEditSession(baseline, () => 'edit-session-a');
  assert.equal(loadScheduledPrefillEditSession(storage), null);
});

test('edit-session cleanup recovery is app-global and retries until confirmation', () => {
  assert.match(appSource, /<ScheduledPrefillEditSessionCleanupRecovery \/>/);
  assert.match(recoverySource, /loadScheduledPrefillEditSession\(sessionStorage\)/);
  assert.match(recoverySource, /cleanupPersistentPrefillEditSession/);
  assert.match(recoverySource, /clearConfirmedEditSession/);
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

test('intent is durable before a start result and cleanup keeps exact ownership', () => {
  const storage = new MemoryStorage();
  const editSession = createScheduledPrefillEditSession(baseline, () => 'edit-session-a');
  const intent = recordEditActionIntent(
    storage,
    editSession,
    'Epic',
    'start',
    null,
    () => 'start-a'
  );

  assert.equal(
    loadScheduledPrefillEditSession(storage)?.services.Epic.start?.editActionId,
    'start-a'
  );
  recordEditSessionStartResult(storage, intent.editSession, 'Epic', 'start-a', 'session-epic');

  const pending = beginEditSessionCleanup(
    storage,
    loadScheduledPrefillEditSession(storage),
    () => 'cleanup-a'
  );
  const request = buildEditSessionCleanupRequest(pending);
  const epic = request.services.find((service) => service.service === 'Epic');

  assert.equal(request.editSessionId, 'edit-session-a');
  assert.equal(request.cleanupId, 'cleanup-a');
  assert.equal(epic?.startSessionId, 'session-epic');
  assert.equal(epic?.baselineSessionId, null);
});

test('baseline work is compensated without claiming its container', () => {
  const storage = new MemoryStorage();
  let editSession = createScheduledPrefillEditSession(baseline, () => 'edit-session-a');
  editSession = recordEditActionIntent(
    storage,
    editSession,
    'Steam',
    'selection',
    'baseline-steam',
    () => 'selection-a'
  ).editSession;
  editSession = recordEditActionIntent(
    storage,
    editSession,
    'Steam',
    'download',
    'baseline-steam',
    () => 'download-a'
  ).editSession;

  const steam = buildEditSessionCleanupRequest(
    beginEditSessionCleanup(storage, editSession, () => 'cleanup-a')
  ).services.find((service) => service.service === 'Steam');

  assert.equal(steam?.baselineSessionId, 'baseline-steam');
  assert.deepEqual(steam?.baselineSelectedAppIds, ['10', '20']);
  assert.equal(steam?.selectionSessionId, 'baseline-steam');
  assert.equal(steam?.prefillSessionId, 'baseline-steam');
  assert.equal(steam?.startSessionId, null);
});

test('download intent also restores the app selection changed by prefill start', () => {
  const storage = new MemoryStorage();
  const editSession = recordEditActionIntent(
    storage,
    createScheduledPrefillEditSession(baseline, () => 'edit-session-a'),
    'Steam',
    'download',
    'baseline-steam',
    () => 'download-a'
  ).editSession;

  const steam = buildEditSessionCleanupRequest(
    beginEditSessionCleanup(storage, editSession, () => 'cleanup-a')
  ).services.find((service) => service.service === 'Steam');

  assert.equal(steam?.prefillSessionId, 'baseline-steam');
  assert.equal(steam?.selectionSessionId, 'baseline-steam');
  assert.deepEqual(steam?.baselineSelectedAppIds, ['10', '20']);
});

test('confirmation clears only the matching edit session and cleanup id', () => {
  const storage = new MemoryStorage();
  const editSession = recordEditActionIntent(
    storage,
    createScheduledPrefillEditSession(baseline, () => 'edit-session-a'),
    'Steam',
    'login',
    'baseline-steam',
    () => 'login-a'
  ).editSession;
  beginEditSessionCleanup(storage, editSession, () => 'cleanup-a');

  assert.equal(clearConfirmedEditSession(storage, 'edit-session-a', 'other-cleanup'), false);
  assert.notEqual(loadScheduledPrefillEditSession(storage), null);
  assert.equal(clearConfirmedEditSession(storage, 'edit-session-a', 'cleanup-a'), true);
  assert.equal(loadScheduledPrefillEditSession(storage), null);
});

test('cleanup suppresses a late start result from recreating active ownership', () => {
  const storage = new MemoryStorage();
  const intent = recordEditActionIntent(
    storage,
    createScheduledPrefillEditSession(baseline, () => 'edit-session-a'),
    'Epic',
    'start',
    null,
    () => 'start-a'
  );
  beginEditSessionCleanup(storage, intent.editSession, () => 'cleanup-a');

  recordEditSessionStartResult(storage, intent.editSession, 'Epic', 'start-a', 'late-session');

  const stored = loadScheduledPrefillEditSession(storage);
  assert.equal(stored?.phase, 'cleanup-pending');
  assert.equal(stored?.services.Epic.start?.returnedSessionId, null);
});

test('committing an edit session clears ownership without creating cleanup work', () => {
  const storage = new MemoryStorage();
  const editSession = recordEditActionIntent(
    storage,
    createScheduledPrefillEditSession(baseline, () => 'edit-session-a'),
    'Steam',
    'selection',
    'baseline-steam',
    () => 'selection-a'
  ).editSession;

  assert.equal(discardCommittedEditSession(storage, editSession.editSessionId), true);
  assert.equal(loadScheduledPrefillEditSession(storage), null);
});
