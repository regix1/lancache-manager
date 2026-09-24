import assert from 'node:assert/strict';
import test, { mock } from 'node:test';
import {
  bindLifted,
  bulkRemovalCard,
  entityBusyFor,
  liftHookCallback,
  loadNotificationModules,
  moduleUrl,
  nextRunRevision,
  operationRunRow as row,
  prefillRunFields,
  pushRun
} from './transpile-module.mjs';

/**
 * The run store decides every run card: one card per run, opened by a run row and ended by a run
 * row, with the per-type events only filling in its text. These cases replay the rows and events
 * of each reported sequence against the real reducers and assert the drawn list after every step,
 * because the defects this store replaces showed up between steps (a second row mid-run, a card
 * left at "waiting"), never only at the end.
 */

const TEMPLATES = {
  'common.errors.signInFailed': 'Failed to sign in to {{platform}}',
  'common.notifications.failedTimesInRow': 'Failed {{formattedCount}} times in a row.',
  'common.notifications.latestRunSucceeded': 'The latest run succeeded.',
  'common.notifications.operationWaitingNamed': '{{name}} is waiting',
  'common.notifications.operationWaitingOn': 'Waiting for {{blocker}} to finish...',
  'common.notifications.operationWaitingOnNamed': '{{name}} is waiting for {{blocker}}',
  'prefill.auth.waitingForSignIn': 'Waiting for {{service}} sign-in',
  'prefill.persistent.services.steam': 'Steam',
  'signalr.gameDetect.error.fatal': 'Game detection failed: {{errorDetail}}',
  'signalr.generic.cancelled': 'Operation cancelled',
  'signalr.generic.failed': 'Operation failed',
  'signalr.generic.nothingToDo': 'Nothing to do',
  'signalr.generic.skipped': 'Operation skipped',
  'signalr.generic.unknown': 'Operation in progress...'
};
const I18N = moduleUrl(`
const templates = ${JSON.stringify(TEMPLATES)};
export default {
  t: (key, options = {}) =>
    (templates[key] ?? key).replace(/{{(\\w+)}}/g, (token, name) =>
      options[name] === undefined ? token : String(options[name])),
  exists: (key) => key in templates || key.startsWith('signalr.')
};`);

const modules = await loadNotificationModules(I18N);
const {
  NOTIFICATION_REGISTRY: notificationEntries,
  applyDetail,
  applyRun,
  applySnapshot,
  buildProgressHandler,
  buildStartedHandler,
  changeSession,
  createRecoveryRunner,
  createRunStoreState,
  deriveNotifications,
  deriveRuns,
  hideRun,
  locateRun,
  nextGeneration,
  readOperationRun,
  readOperationRunsSnapshot,
  releaseKeptSuccess,
  removeRuns,
  setRunCancel,
  settleBulkCards
} = modules;

// ── Rows as the server sends them (`operationRunRow`, shared with the other store tests) ──

/** The server keeps these endings until someone closes them. [47] */
const kept = (operationId, fields) => row(operationId, { retained: true, ...fields });

/**
 * One browser: its store, its own cards, the auth session it is signed in with, and the request
 * numbers its provider would issue.
 */
class Browser {
  constructor({ keepSuccessVisible = false, localCards = [], sessionId = null } = {}) {
    this.state = createRunStoreState();
    this.keepSuccessVisible = keepSuccessVisible;
    this.localCards = localCards;
    this.sessionId = sessionId;
    this.issued = 0;
    this.ended = [];
    this.merged = [];
  }

  record(result) {
    this.state = result.next;
    this.ended.push(...result.ended);
    this.merged.push(...result.merged);
    return result;
  }

  push(value) {
    return this.record(
      applyRun(this.state, value, {
        keepSuccessVisible: this.keepSuccessVisible,
        localCards: this.localCards,
        pushed: true,
        sessionId: this.sessionId
      })
    );
  }

  /** A snapshot request issued now; `apply` delivers its response. */
  request() {
    const requestSeq = ++this.issued;
    return (runs, snapshotRevision = nextRunRevision()) =>
      this.record(
        applySnapshot(
          this.state,
          { runs, revision: snapshotRevision },
          {
            keepSuccessVisible: this.keepSuccessVisible,
            localCards: this.localCards,
            requestSeq,
            issuedSeq: this.issued,
            sessionId: this.sessionId
          }
        )
      );
  }

  snapshot(runs, snapshotRevision) {
    return this.request()(runs, snapshotRevision);
  }

  reconnect() {
    this.state = nextGeneration(this.state);
  }

  /** Signed out (`null`) or signed in as another session; no run list is read here. */
  changeSession(sessionId) {
    this.state = changeSession(this.state, sessionId);
    this.sessionId = sessionId;
  }

  detail(operationId, patch, source = 'event') {
    this.state = applyDetail(this.state, operationId, () => patch, source, {
      requestSeq: this.issued
    });
  }

  /** The provider's hold and 300 ms fade finishing for every card that is leaving. */
  fade() {
    const leaving = [...this.state.entries.values()]
      .filter((entry) => entry.leaving)
      .map((entry) => entry.run.operationId);
    this.state = removeRuns(this.state, leaving);
  }

  cards() {
    return deriveNotifications(this.state, this.localCards);
  }

  card(id) {
    return this.cards().find((card) => card.id === id);
  }

  drawn() {
    return this.cards().map(
      (card) => `${card.id}:${card.type}:${card.status}${card.controlOnly ? ':row' : ''}`
    );
  }
}

// ── The reported sequences (context/diagnostic.md, Reproduction) ────────────

test('eviction continued on its waiting id shows one row that waits, runs, and is gone', () => {
  const browser = new Browser();
  const waiting = { status: 'waiting', visibility: 'background' };
  browser.push(row('W', { ...waiting, blockedByName: 'Cache File Scan' }));
  assert.deepEqual(browser.drawn(), ['W:eviction_scan:waiting:row']);
  // The row prints the run's name beside its message, so the message names only the blocker.
  assert.equal(browser.card('W').message, 'Waiting for Cache File Scan to finish...');
  browser.push(row('W', { ...waiting, blockedByName: 'Game Removal' }));
  assert.equal(browser.card('W').message, 'Waiting for Game Removal to finish...');

  // A full waiting card has no name beside it, so its sentence carries the run's name.
  const full = new Browser();
  full.push(row('C', { status: 'waiting', blockedByName: 'Game Removal' }));
  assert.equal(full.card('C').message, 'Eviction Scan is waiting for Game Removal');

  browser.push(row('W', { visibility: 'background', percentComplete: 20 }));
  assert.deepEqual(browser.drawn(), ['W:eviction_scan:running:row']);

  browser.push(row('W', { status: 'completed', visibility: 'background', percentComplete: 100 }));
  assert.deepEqual(browser.drawn(), [], 'a finished background row leaves at once');
  browser.fade();
  assert.deepEqual(browser.drawn(), []);
  assert.equal(browser.state.entries.size, 0, 'nothing is left behind');
});

for (const operationType of ['cacheSizeScan', 'gameDetection']) {
  test(`${operationType} promoted to a new id keeps one row the whole time`, () => {
    const browser = new Browser();
    browser.push(row('W', { operationType, status: 'waiting', visibility: 'background' }));
    assert.deepEqual(browser.drawn().length, 1);

    browser.push(row('N', { operationType, visibility: 'background', previousOperationId: 'W' }));
    assert.equal(browser.cards().length, 1);
    const card = browser.cards()[0];
    assert.equal(card.id, 'W', 'the card keeps the first operation id of the chain');
    assert.equal(card.details.operationId, 'N', 'details.operationId follows the work');
    assert.deepEqual(card.details.operationIds, ['W', 'N']);
    assert.deepEqual(browser.merged, [{ from: 'W', to: 'N' }]);

    // The waiting run's own terminal row arrives after the merge and changes nothing.
    browser.push(
      row('W', {
        operationType,
        status: 'completed',
        visibility: 'background',
        nextOperationId: 'N'
      })
    );
    assert.equal(browser.cards().length, 1);
    assert.equal(browser.cards()[0].id, 'W');

    browser.push(row('N', { operationType, status: 'completed', visibility: 'background' }));
    browser.fade();
    assert.deepEqual(browser.drawn(), []);
  });
}

test('a handoff row seen before its successor merges when the successor arrives', () => {
  const browser = new Browser();
  browser.push(row('W', { operationType: 'gameDetection', status: 'waiting' }));
  browser.push(
    row('W', { operationType: 'gameDetection', status: 'completed', nextOperationId: 'N' })
  );
  assert.deepEqual(
    browser.drawn(),
    ['W:game_detection:waiting'],
    'the card stays as it was while the browser is unsure where the work went'
  );
  assert.deepEqual(browser.state.links.get('N'), ['W']);

  browser.push(row('N', { operationType: 'gameDetection' }));
  assert.deepEqual(browser.drawn(), ['W:game_detection:running']);
  assert.equal(browser.card('W').details.operationId, 'N');
  assert.deepEqual(browser.merged, [{ from: 'W', to: 'N' }]);
});

test('cancelling a Silent run while it waits removes its row and leaves nothing', () => {
  const browser = new Browser();
  browser.push(row('W', { status: 'waiting', visibility: 'background' }));
  browser.push(row('W', { status: 'cancelled', visibility: 'background' }));
  assert.deepEqual(browser.drawn(), [], 'a fading row is not drawn');
  browser.fade();
  assert.equal(browser.state.entries.size, 0);
  assert.deepEqual(browser.ended, [{ operationId: 'W', status: 'cancelled', error: undefined }]);
});

test('a waiting card keeps its blocker while it is cancelled, then says how it ended', () => {
  const browser = new Browser();
  const waiting = { status: 'waiting', blockedByName: 'Game Removal' };
  browser.push(row('W', waiting));
  assert.equal(browser.card('W').message, 'Eviction Scan is waiting for Game Removal');

  // The tracker's own sentences, never keys: no worker ever describes a parked run.
  browser.push(
    row('W', { ...waiting, status: 'cancelling', message: 'Cancellation requested...' })
  );
  assert.deepEqual(browser.drawn(), ['W:eviction_scan:cancelling']);
  assert.equal(browser.card('W').message, 'Eviction Scan is waiting for Game Removal');

  browser.push(row('W', { ...waiting, status: 'cancelled', message: 'Operation cancelled' }));
  assert.deepEqual(browser.drawn(), ['W:eviction_scan:cancelled']);
  assert.equal(browser.card('W').message, 'Operation cancelled');

  for (const [status, message, shown] of [
    // A skip's row message is the reason the server kept, the same text the live card showed.
    ['skipped', 'No downloads in database', 'No downloads in database'],
    ['failed', 'Disk read failed', 'Operation failed'],
    // The eviction scan's Run Now that found no downloads: its only text is the row's stage key.
    ['skipped', 'signalr.generic.nothingToDo', 'Nothing to do']
  ]) {
    const ended = new Browser();
    ended.push(row('E', { status: 'waiting' }));
    ended.push(kept('E', { status, message }));
    assert.equal(ended.card('E').message, shown, message);
  }
});

test('a missed game detection end never blocks the next run, and the stale card ends on return', () => {
  for (const returnKind of ['reconnect', 'tab return']) {
    const browser = new Browser();
    browser.push(row('A', { operationType: 'gameDetection' }));
    // A's end is missed; B is a separate run and gets its own card.
    browser.push(row('B', { operationType: 'gameDetection' }));
    assert.deepEqual(browser.drawn(), ['A:game_detection:running', 'B:game_detection:running']);

    if (returnKind === 'reconnect') browser.reconnect();
    browser.snapshot([row('B', { operationType: 'gameDetection', percentComplete: 40 })]);
    assert.deepEqual(browser.drawn(), ['B:game_detection:running'], returnKind);
    assert.ok(browser.ended.some((end) => end.operationId === 'A' && end.status === 'gone'));

    browser.push(row('B', { operationType: 'gameDetection', status: 'completed' }));
    browser.fade();
    assert.deepEqual(
      browser.drawn(),
      [],
      'no card stays at running after the server said it ended'
    );
  }
});

// ── Endings and notification modes [6] [7] [8] ──────────────────────────────

test('with Keep Notifications Visible off, only red and amber cards stay, and no timer ends a card', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const browser = new Browser();
    browser.push(row('OK'));
    browser.push(row('OK', { status: 'completed' }));
    assert.equal(browser.card('OK').status, 'completed');
    assert.equal(browser.state.entries.get('OK').leaving, true, 'the success starts its fade');

    // Unscheduled types, so the one-card-per-schedule rule does not fold these together.
    browser.push(row('F', { operationType: 'cacheClearing' }));
    browser.push(
      kept('F', { operationType: 'cacheClearing', status: 'failed', error: 'Disk full' })
    );
    browser.push(row('C', { operationType: 'dataImport' }));
    browser.push(row('C', { operationType: 'dataImport', status: 'cancelled' }));
    assert.equal(browser.card('C').details.cancelled, true);
    assert.equal(browser.state.entries.get('C').leaving, true, 'the cancel starts its fade');
    browser.push(row('S', { operationType: 'gameRemoval' }));
    browser.push(kept('S', { operationType: 'gameRemoval', status: 'skipped' }));
    browser.push(row('WARN'));
    browser.push(kept('WARN', { status: 'completed', warning: 'boom' }));

    mock.timers.tick(10 * 60 * 1000);
    browser.fade();
    assert.deepEqual(browser.drawn(), [
      'F:cache_clearing:failed',
      'S:game_removal:skipped',
      'WARN:eviction_scan:completed'
    ]);
    assert.equal(browser.card('F').error, 'Disk full');
    assert.equal(browser.card('WARN').details.notificationType, 'warning');
  } finally {
    mock.timers.reset();
  }
});

test('with Keep Notifications Visible on, every finished card stays until the setting is turned off', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const browser = new Browser({ keepSuccessVisible: true });
    browser.push(row('OK'));
    browser.push(row('OK', { status: 'completed' }));
    browser.push(row('F', { operationType: 'cacheClearing' }));
    browser.push(kept('F', { operationType: 'cacheClearing', status: 'failed' }));
    browser.push(row('C', { operationType: 'dataImport' }));
    browser.push(row('C', { operationType: 'dataImport', status: 'cancelled' }));
    mock.timers.tick(10 * 60 * 1000);
    assert.deepEqual(browser.drawn(), [
      'OK:eviction_scan:completed',
      'F:cache_clearing:failed',
      'C:data_import:cancelled'
    ]);

    browser.state = releaseKeptSuccess(browser.state);
    assert.equal(browser.state.entries.get('OK').leaving, true);
    assert.equal(browser.state.entries.get('C').leaving, true);
    browser.fade();
    assert.deepEqual(browser.drawn(), ['F:cache_clearing:failed']);
  } finally {
    mock.timers.reset();
  }
});

test('each notification mode draws its card, row or nothing, and a failure is a red card in every mode', () => {
  const expected = {
    card: {
      live: ['R:{type}:running'],
      completed: ['R:{type}:completed'],
      failed: ['R:{type}:failed'],
      cancelled: ['R:{type}:cancelled'],
      skipped: ['R:{type}:skipped']
    },
    background: {
      live: ['R:{type}:running:row'],
      completed: [],
      failed: ['R:{type}:failed'],
      cancelled: [],
      skipped: []
    },
    hidden: { live: [], completed: [], failed: ['R:{type}:failed'], cancelled: [], skipped: [] }
  };
  for (const [operationType, type] of [
    ['evictionScan', 'eviction_scan'],
    // The eviction scan's own silent cleanup: hidden while it runs, a red card if it fails.
    ['evictionRemoval', 'eviction_removal']
  ]) {
    for (const visibility of ['card', 'background', 'hidden']) {
      for (const outcome of ['live', 'completed', 'failed', 'cancelled', 'skipped']) {
        const browser = new Browser();
        browser.push(row('R', { operationType, visibility }));
        if (outcome !== 'live')
          browser.push(
            row('R', {
              operationType,
              visibility,
              status: outcome,
              // The server keeps what stays until closed; the browser never recomputes it.
              retained: outcome === 'failed' || (outcome === 'skipped' && visibility === 'card')
            })
          );
        const want = expected[visibility][outcome].map((text) => text.replace('{type}', type));
        assert.deepEqual(browser.drawn(), want, `${operationType} ${visibility} ${outcome}`);
        if (outcome === 'live')
          assert.equal(deriveRuns(browser.state).length, 1, 'a live run keeps its buttons busy');
      }
    }
  }
});

test('a run that succeeded with a warning is an amber card in every mode, first seen or not', () => {
  for (const visibility of ['card', 'background', 'hidden']) {
    for (const keepSuccessVisible of [false, true]) {
      for (const seenStart of [true, false]) {
        mock.timers.enable({ apis: ['setTimeout'] });
        try {
          const browser = new Browser({ keepSuccessVisible });
          if (seenStart) browser.push(row('R', { visibility }));
          const ending = kept('R', { visibility, status: 'completed', warning: 'boom' });
          if (seenStart) browser.push(ending);
          else browser.snapshot([ending]);
          mock.timers.tick(10 * 60 * 1000);
          browser.state = releaseKeptSuccess(browser.state);
          browser.fade();
          const label = `${visibility} keep=${keepSuccessVisible} seen=${seenStart}`;
          assert.deepEqual(browser.drawn(), ['R:eviction_scan:completed'], label);
          const card = browser.card('R');
          assert.equal(card.details.notificationType, 'warning', label);
          assert.equal(card.detailMessage, 'Game detection failed: boom', label);
        } finally {
          mock.timers.reset();
        }
      }
    }
  }

  const plain = new Browser();
  plain.push(row('R'));
  plain.push(row('R', { status: 'completed' }));
  plain.fade();
  assert.deepEqual(plain.drawn(), [], 'a success without a warning follows the plain rule');
});

// ── Kept endings and one card per schedule [47] [50] ────────────────────────

test('a failure missed while disconnected is drawn from the snapshot and closes everywhere', () => {
  for (const arrival of ['reconnect', 'reload']) {
    const browser = new Browser();
    if (arrival === 'reconnect') browser.reconnect();
    const failure = kept('F', { status: 'failed', error: 'Boom' });
    browser.snapshot([failure]);
    assert.deepEqual(browser.drawn(), ['F:eviction_scan:failed'], arrival);
    assert.deepEqual(browser.card('F').details.closeOperationIds, ['F']);

    // Another admin closed it.
    browser.push({ ...failure, closed: true, revision: nextRunRevision() });
    browser.fade();
    assert.deepEqual(browser.drawn(), [], arrival);
  }

  const missed = new Browser();
  missed.snapshot([kept('K', { status: 'skipped' })]);
  assert.deepEqual(missed.drawn(), ['K:eviction_scan:skipped']);
  // Its closed row was missed; the next snapshot no longer holds it.
  missed.snapshot([]);
  missed.fade();
  assert.deepEqual(missed.drawn(), [], 'a kept card missing from a later snapshot is removed');
});

test('a kept ending updates in place, and a closed id this browser recorded ignores its row', () => {
  const browser = new Browser();
  browser.push(row('F', { operationType: 'gameDetection' }));
  const first = kept('F', { operationType: 'gameDetection', status: 'failed' });
  browser.push(first);
  browser.push({ ...first, consecutiveFailures: 2, revision: nextRunRevision() });
  assert.deepEqual(browser.drawn(), ['F:game_detection:failed']);
  assert.equal(browser.card('F').detailMessage, 'Failed 2 times in a row.');

  // The dismiss closed it on the server; the store records exactly that id.
  browser.state = removeRuns(browser.state, ['F']);
  browser.push({ ...first, closed: true, revision: nextRunRevision() });
  browser.push({ ...first, revision: nextRunRevision() });
  assert.deepEqual(browser.drawn(), []);
});

test('a card a cancel removed as already finished comes back when the server kept its ending', () => {
  // The run failed on the server; its terminal row was still on the way when the cancel's
  // "already finished" answer removed the live card.
  const pushed = new Browser();
  pushed.push(row('R'));
  pushed.state = removeRuns(pushed.state, ['R']);
  assert.deepEqual(pushed.drawn(), []);
  pushed.push(kept('R', { status: 'failed', error: 'Boom' }));
  assert.deepEqual(pushed.drawn(), ['R:eviction_scan:failed']);

  const snapshotted = new Browser();
  snapshotted.push(row('S'));
  snapshotted.state = removeRuns(snapshotted.state, ['S']);
  snapshotted.snapshot([kept('S', { status: 'failed' })]);
  assert.deepEqual(snapshotted.drawn(), ['S:eviction_scan:failed']);

  // A plain success removed the same way stays gone.
  snapshotted.push(row('P'));
  snapshotted.state = removeRuns(snapshotted.state, ['P']);
  snapshotted.push(row('P', { status: 'completed' }));
  assert.deepEqual(snapshotted.drawn(), ['S:eviction_scan:failed']);

  // A late copy of a kept row, stamped before a snapshot that no longer held the run (closed and
  // reaped), stays out.
  const late = new Browser();
  late.push(row('L'));
  const lateFailure = kept('L', { status: 'failed' });
  late.snapshot([]);
  late.push(lateFailure);
  assert.deepEqual(late.drawn(), []);
});

test('one kept card per schedule: the older ending is gone in the same apply', () => {
  const browser = new Browser();
  const schedule = { operationType: 'gameDetection' };
  browser.push(row('A', schedule));
  const endA = kept('A', { ...schedule, status: 'failed' });
  browser.push(endA);
  assert.deepEqual(browser.drawn(), ['A:game_detection:failed']);

  browser.push(row('B', schedule));
  const endB = kept('B', { ...schedule, status: 'failed' });
  browser.push(endB);
  assert.deepEqual(browser.drawn(), ['B:game_detection:failed'], 'never two failure cards');

  browser.push({ ...endB, consecutiveFailures: 2, revision: nextRunRevision() });
  assert.equal(browser.card('B').detailMessage, 'Failed 2 times in a row.');

  browser.push({ ...endA, closed: true, revision: nextRunRevision() });
  assert.deepEqual(browser.drawn(), ['B:game_detection:failed'], "A's closed row changes nothing");

  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    browser.push({
      ...endB,
      consecutiveFailures: 2,
      latestRunSucceeded: true,
      revision: nextRunRevision()
    });
    mock.timers.tick(10 * 60 * 1000);
    assert.deepEqual(browser.drawn(), ['B:game_detection:failed']);
    assert.equal(
      browser.card('B').detailMessage,
      'Failed 2 times in a row. The latest run succeeded.'
    );
  } finally {
    mock.timers.reset();
  }
});

test('prefill schedules are told apart by schedule id', () => {
  const browser = new Browser();
  const prefill = (id, scheduleId) =>
    kept(id, {
      operationType: 'scheduledPrefill',
      status: 'failed',
      serviceId: 'Steam',
      scheduleId
    });
  browser.push(prefill('P1', 'S1'));
  browser.push(prefill('P2', 'S2'));
  assert.deepEqual(browser.drawn(), ['P1:scheduled_prefill:failed', 'P2:scheduled_prefill:failed']);
  browser.push(prefill('P3', 'S1'));
  assert.deepEqual(browser.drawn(), ['P2:scheduled_prefill:failed', 'P3:scheduled_prefill:failed']);
});

test('live log ingest keeps one red card, replaced in the same apply, and marks nothing busy', () => {
  const browser = new Browser();
  const ingest = {
    operationType: 'logProcessing',
    name: 'Log Processing',
    visibility: 'hidden',
    liveIngest: true
  };
  const failure = (id) =>
    kept(id, {
      ...ingest,
      status: 'failed',
      message: 'Log processing failed with exit code 1',
      error: 'Log processing failed with exit code 1'
    });
  const first = failure('L1');
  browser.push(first);
  assert.deepEqual(browser.drawn(), ['L1:log_processing:failed']);
  assert.equal(browser.card('L1').message, 'Operation failed');
  assert.deepEqual(deriveRuns(browser.state), [], 'no page button is busy');

  browser.push(failure('L2'));
  assert.deepEqual(browser.drawn(), ['L2:log_processing:failed'], 'never two ingest failure cards');
  browser.push({ ...first, closed: true, revision: nextRunRevision() });
  assert.deepEqual(
    browser.drawn(),
    ['L2:log_processing:failed'],
    "L1's closed row changes nothing"
  );
  assert.deepEqual(deriveRuns(browser.state), []);

  // A log processing run someone started keeps its own failure card, Hidden or not: only the
  // row's live-ingest flag groups a failure with the ingest card.
  for (const [id, visibility] of [
    ['U', 'card'],
    ['H', 'hidden']
  ]) {
    const started = { operationType: 'logProcessing', name: 'Log Processing', visibility };
    browser.push(row(id, started));
    assert.equal(
      deriveRuns(browser.state).length,
      1,
      `a ${visibility} run someone started keeps its buttons busy`
    );
    browser.push(kept(id, { ...started, status: 'failed' }));
  }
  assert.deepEqual(browser.drawn(), [
    'L2:log_processing:failed',
    'U:log_processing:failed',
    'H:log_processing:failed'
  ]);
});

test('live log ingest keeps no record of a pass, and a live pass is never drawn or busy', () => {
  const browser = new Browser();
  const ingest = {
    operationType: 'logProcessing',
    name: 'Log Processing',
    visibility: 'hidden',
    liveIngest: true
  };
  browser.push(row('R', { operationType: 'gameDetection' }));
  const entries = browser.state.entries.size;
  const ended = browser.state.ended.size;
  // A tab left open through hours of downloads sees a pass every few seconds.
  for (let pass = 0; pass < 500; pass++) {
    browser.push(row(`P${pass}`, ingest));
    assert.deepEqual(browser.drawn(), ['R:game_detection:running']);
    assert.deepEqual(
      deriveRuns(browser.state).map((run) => run.id),
      ['R'],
      'a live pass marks no button busy'
    );
    browser.push(row(`P${pass}`, { ...ingest, status: 'completed' }));
  }
  assert.equal(browser.state.entries.size, entries);
  assert.equal(browser.state.ended.size, ended);

  browser.push(kept('F', { ...ingest, status: 'failed' }));
  assert.deepEqual(browser.drawn(), ['R:game_detection:running', 'F:log_processing:failed']);
  assert.equal(browser.state.entries.size, entries + 1, 'a kept failure is stored');
});

test('a kept skip or warning of a schedule leaves its failure card in place', () => {
  const schedule = { operationType: 'gameDetection' };
  for (const later of [{ status: 'skipped' }, { status: 'completed', warning: 'boom' }]) {
    const browser = new Browser();
    browser.push(kept('F', { ...schedule, status: 'failed' }));
    browser.push(kept('L', { ...schedule, ...later }));
    assert.deepEqual(
      browser.drawn(),
      ['F:game_detection:failed', `L:game_detection:${later.status}`],
      later.status
    );

    // A newer ending of the same kind replaces the older one, so a schedule never collects a card
    // per run, and the failure card stays.
    browser.push(kept('M', { ...schedule, ...later }));
    assert.deepEqual(
      browser.drawn(),
      ['F:game_detection:failed', `M:game_detection:${later.status}`],
      later.status
    );

    // A newer failure still replaces the failure card, and only that one.
    browser.push(kept('N', { ...schedule, status: 'failed' }));
    assert.deepEqual(
      browser.drawn(),
      [`M:game_detection:${later.status}`, 'N:game_detection:failed'],
      later.status
    );
  }
});

test('the ending order is the server one, whatever order the rows arrive in', () => {
  const schedule = { operationType: 'gameDetection' };
  // A started first but ended last: B was queued, failed and kept before A finished.
  const endB = kept('B', { ...schedule, status: 'failed', completedRevision: 30 });
  const endA = kept('A', { ...schedule, status: 'failed', completedRevision: 40 });

  const pushed = new Browser();
  pushed.push(endB);
  pushed.push(endA);
  assert.deepEqual(pushed.drawn(), ['A:game_detection:failed']);
  pushed.push({ ...endB, closed: true, revision: nextRunRevision() });
  assert.deepEqual(pushed.drawn(), ['A:game_detection:failed'], "B's closed row changes nothing");
  pushed.reconnect();
  pushed.snapshot([endA]);
  assert.deepEqual(pushed.drawn(), ['A:game_detection:failed']);

  for (const order of [
    [endA, endB],
    [endB, endA]
  ]) {
    const reloaded = new Browser();
    reloaded.snapshot(order);
    assert.deepEqual(reloaded.drawn(), ['A:game_detection:failed'], 'both in one snapshot');
  }

  const closedFirst = new Browser();
  closedFirst.push(endB);
  closedFirst.push({ ...endB, closed: true, revision: nextRunRevision() });
  closedFirst.push(endA);
  closedFirst.fade();
  assert.deepEqual(closedFirst.drawn(), ['A:game_detection:failed']);
});

test('a schedule keeps one kept ending of each kind, and an ending never closes another kind', () => {
  const schedule = { operationType: 'gameDetection' };
  const endings = {
    skip: { status: 'skipped' },
    warning: { status: 'completed', warning: 'boom' },
    failure: { status: 'failed' }
  };
  for (const steps of [
    [
      ['skip', ['E0:game_detection:skipped']],
      ['warning', ['E0:game_detection:skipped', 'E1:game_detection:completed']]
    ],
    [
      ['warning', ['E0:game_detection:completed']],
      ['skip', ['E0:game_detection:completed', 'E1:game_detection:skipped']]
    ],
    [
      ['failure', ['E0:game_detection:failed']],
      ['skip', ['E0:game_detection:failed', 'E1:game_detection:skipped']],
      ['failure', ['E1:game_detection:skipped', 'E2:game_detection:failed']]
    ]
  ]) {
    const browser = new Browser();
    const label = steps.map(([kind]) => kind).join(', ');
    steps.forEach(([kind, want], index) => {
      browser.push(kept(`E${index}`, { ...schedule, ...endings[kind] }));
      assert.deepEqual(browser.drawn(), want, `${label}: after ${kind} ${index}`);
    });
  }
});

test('a mapping sign-in failure and its schedule failure are two kept cards, in either order', () => {
  const mapping = { operationType: 'xboxMapping', name: 'Xbox Mapping', status: 'failed' };
  const signIn = kept('S', { ...mapping, integrationLogin: true, error: 'Sign-in failed' });
  const schedule = kept('F', { ...mapping, consecutiveFailures: 2 });
  for (const order of [
    [signIn, schedule],
    [schedule, signIn]
  ]) {
    const browser = new Browser();
    for (const ending of order) browser.push(ending);
    const want = order.map((ending) => `${ending.operationId}:xbox_game_mapping:failed`);
    assert.deepEqual(browser.drawn(), want, `${want[0]} first`);
    assert.equal(browser.card('F').detailMessage, 'Failed 2 times in a row.');
    assert.equal(browser.card('S').detailMessage, undefined);

    // The schedule's next failure replaces only the schedule's card.
    browser.push(kept('F2', mapping));
    assert.deepEqual(
      browser.drawn(),
      [...want.filter((card) => !card.startsWith('F:')), 'F2:xbox_game_mapping:failed'],
      `${want[0]} first`
    );
  }
});

// ── Prefill sign-in, drawn only by the browser that started it [64] ─────────

const signIn = {
  operationType: 'prefillLogin',
  name: 'Prefill Login',
  message: 'Waiting for sign-in',
  serviceId: 'Steam',
  ownerSessionId: 'session-a',
  // A sign-in has no percentage, whatever the row carries.
  percentComplete: 40
};

test('a prefill sign-in draws one card from its row, in the browser whose session started it', () => {
  for (const arrival of ['push', 'reload']) {
    const owner = new Browser({ sessionId: 'session-a' });
    if (arrival === 'push') owner.push(row('L', signIn));
    else owner.snapshot([row('L', signIn)]);
    assert.deepEqual(owner.drawn(), ['L:prefill_login:running'], arrival);
    const card = owner.card('L');
    assert.equal(card.message, 'Waiting for Steam sign-in');
    assert.equal(card.progress, undefined, 'no bar on the full card');
    assert.equal(card.progressMode, undefined);
    assert.deepEqual(
      deriveRuns(owner.state).map((run) => run.id),
      ['L']
    );
  }

  for (const sessionId of ['session-b', null]) {
    const other = new Browser({ sessionId });
    other.push(row('L', signIn));
    other.snapshot([row('L', signIn)]);
    assert.deepEqual(other.drawn(), [], `session ${sessionId}`);
    assert.deepEqual(deriveRuns(other.state), [], 'no button turns busy there');
    assert.equal(other.state.entries.size, 0);
  }
});

test('a prefill sign-in stays after a failure, and otherwise leaves unless kept visible', () => {
  for (const status of ['completed', 'cancelled']) {
    for (const keepSuccessVisible of [false, true]) {
      const browser = new Browser({ sessionId: 'session-a', keepSuccessVisible });
      browser.push(row('L', signIn));
      browser.push(row('L', { ...signIn, status }));
      browser.fade();
      assert.deepEqual(
        browser.drawn(),
        keepSuccessVisible ? [`L:prefill_login:${status}`] : [],
        `${status} keep=${keepSuccessVisible}`
      );
    }
  }
  const failed = new Browser({ sessionId: 'session-a' });
  failed.push(row('L', signIn));
  failed.push(kept('L', { ...signIn, status: 'failed', error: 'Sign-in failed' }));
  failed.fade();
  assert.deepEqual(failed.drawn(), ['L:prefill_login:failed']);
});

test('a sign-in the daemon refused names its platform, with the server reason below', () => {
  const failed = new Browser({ sessionId: 'session-a' });
  failed.push(row('L', signIn));
  failed.push(kept('L', { ...signIn, status: 'failed', error: 'Wrong password' }));
  assert.equal(failed.card('L').message, 'Failed to sign in to Steam');
  assert.equal(failed.card('L').error, 'Wrong password');
});

test('a session change ends the earlier session sign-in card with no run list read', () => {
  for (const nextSession of ['session-b', null]) {
    for (const retained of [false, true]) {
      const label = `to ${nextSession}, ${retained ? 'kept failure' : 'waiting'}`;
      const browser = new Browser({ sessionId: 'session-a' });
      browser.push(row('R', { operationType: 'gameDetection' }));
      browser.push(row('L', signIn));
      if (retained) browser.push(kept('L', { ...signIn, status: 'failed' }));
      assert.equal(browser.drawn().length, 2, label);

      browser.changeSession(nextSession);
      assert.deepEqual(browser.drawn(), ['R:game_detection:running'], label);
      assert.deepEqual(
        deriveRuns(browser.state).map((run) => run.id),
        ['R'],
        label
      );

      // Signing in as the owner again draws the row the next run list holds.
      browser.changeSession('session-a');
      browser.snapshot([
        row('R', { operationType: 'gameDetection' }),
        retained ? kept('L', { ...signIn, status: 'failed' }) : row('L', signIn)
      ]);
      assert.deepEqual(
        browser.drawn(),
        ['R:game_detection:running', `L:prefill_login:${retained ? 'failed' : 'running'}`],
        label
      );
    }
  }
});

// ── A card closed on this screen while the server is unreachable [172] [173] ──

test('a card closed while disconnected stays busy and comes back only while the server still runs it', () => {
  const browser = new Browser();
  const epic = { ...prefillRunFields, serviceId: 'Epic', scheduleId: 'S2' };
  browser.push(row('P1', prefillRunFields));
  browser.push(row('P2', epic));
  browser.push(row('P3'));
  for (const id of ['P1', 'P2', 'P3']) browser.state = hideRun(browser.state, id);
  assert.deepEqual(browser.drawn(), []);
  assert.deepEqual(
    deriveRuns(browser.state).map((run) => run.id),
    ['P1', 'P2', 'P3'],
    'every run keeps its page busy'
  );

  browser.reconnect();
  browser.snapshot([row('P1', prefillRunFields), kept('P2', { ...epic, status: 'failed' })]);
  assert.deepEqual(browser.drawn(), [
    'P1:scheduled_prefill:running',
    'P2:scheduled_prefill:failed'
  ]);
  assert.deepEqual(
    deriveRuns(browser.state).map((run) => run.id),
    ['P1'],
    'a run the list no longer holds is gone'
  );
  assert.ok(browser.ended.some((end) => end.operationId === 'P3' && end.status === 'gone'));
});

test('closing a card that already ended, or an unknown one, changes nothing', () => {
  const browser = new Browser();
  browser.push(row('F'));
  browser.push(kept('F', { status: 'failed' }));
  for (const id of ['F', 'NOPE']) assert.equal(hideRun(browser.state, id), browser.state, id);
});

test('a card closed while its cancel was in flight comes back without the cancel', () => {
  const browser = new Browser();
  browser.push(row('P1', prefillRunFields));
  browser.state = setRunCancel(browser.state, 'P1', {
    cancelRequested: true,
    cancelSent: true,
    cancelPending: true
  });
  assert.deepEqual(browser.drawn(), ['P1:scheduled_prefill:cancelling']);
  browser.state = hideRun(browser.state, 'P1');
  browser.reconnect();
  browser.snapshot([row('P1', prefillRunFields)]);
  assert.deepEqual(browser.drawn(), ['P1:scheduled_prefill:running']);
  assert.notEqual(browser.card('P1').details.cancelPending, true);
});

test('(q) a progress detail brings back a card closed while disconnected', () => {
  const browser = new Browser();
  browser.push(row('P'));
  browser.state = hideRun(browser.state, 'P');
  assert.deepEqual(browser.drawn(), []);
  browser.detail('P', { message: 'Scanning' });
  assert.deepEqual(browser.drawn(), ['P:eviction_scan:running']);
});

test('a completion detail leaves a closed card hidden until the run row says how it ended', () => {
  const browser = new Browser();
  browser.push(row('P'));
  browser.state = hideRun(browser.state, 'P');
  // The run list read after the reconnect failed, so no row has spoken for the run yet.
  browser.reconnect();
  browser.detail('P', { message: 'Done' }, 'completion');
  assert.deepEqual(browser.drawn(), []);
  browser.push(row('P', { status: 'completed' }));
  assert.deepEqual(browser.drawn(), ['P:eviction_scan:completed']);
});

// ── Bulk removal [16] ───────────────────────────────────────────────────────

test('a bulk removal owns its items: one purple card while an item waits, no item card', () => {
  const browser = new Browser({
    localCards: [bulkRemovalCard({ currentOperationId: 'I1', itemOperationIds: ['I1'] })]
  });
  browser.push(
    row('I1', {
      operationType: 'gameRemoval',
      name: 'Game Removal',
      status: 'waiting',
      blockedByName: 'Eviction Scan'
    })
  );
  assert.deepEqual(browser.drawn(), ['bulk:bulk_removal:waiting']);
  assert.equal(browser.card('bulk').message, 'Game Removal is waiting for Eviction Scan');

  browser.push(row('I1', { operationType: 'gameRemoval' }));
  assert.deepEqual(browser.drawn(), ['bulk:bulk_removal:running']);
  assert.equal(browser.card('bulk').message, 'Removing 1 of 3');
  assert.equal(deriveRuns(browser.state).length, 1, 'the item still keeps its row busy');
});

test('a failed item stays folded in its batch card after the batch moves on and ends', () => {
  const browser = new Browser({
    localCards: [bulkRemovalCard({ currentOperationId: 'I1', itemOperationIds: ['I1'] })]
  });
  browser.push(row('I1', { operationType: 'gameRemoval' }));
  browser.push(kept('I1', { operationType: 'gameRemoval', status: 'failed' }));
  let settled = settleBulkCards(browser.state, browser.localCards);
  browser.state = settled.next;
  assert.deepEqual(browser.drawn(), ['bulk:bulk_removal:running']);
  assert.deepEqual(browser.card('bulk').details.closeOperationIds, ['I1']);

  browser.localCards = [
    bulkRemovalCard({ currentOperationId: 'I2', itemOperationIds: ['I1', 'I2'] })
  ];
  browser.push(row('I2', { operationType: 'gameRemoval' }));
  browser.push(row('I2', { operationType: 'gameRemoval', status: 'completed' }));
  browser.fade();
  assert.deepEqual(browser.drawn(), ['bulk:bulk_removal:running']);
  assert.deepEqual(browser.card('bulk').details.closeOperationIds, ['I1']);

  browser.localCards = [bulkRemovalCard({ itemOperationIds: ['I1', 'I2'] }, { status: 'failed' })];
  settled = settleBulkCards(browser.state, browser.localCards);
  browser.state = settled.next;
  assert.deepEqual(settled.release, [], 'a kept failure still folded keeps the batch card');
  assert.deepEqual(browser.card('bulk').details.closeOperationIds, ['I1']);

  // Another admin closes the last kept item: the finished batch card leaves.
  browser.push({
    ...kept('I1', { operationType: 'gameRemoval', status: 'failed' }),
    closed: true
  });
  settled = settleBulkCards(browser.state, browser.localCards);
  assert.deepEqual(settled.release, ['bulk']);
});

test('the last kept item closed elsewhere while the batch still runs releases it when it ends', () => {
  for (const failedWithoutRun of [false, true]) {
    const browser = new Browser({
      localCards: [bulkRemovalCard({ currentOperationId: 'I1', itemOperationIds: ['I1'] })]
    });
    browser.push(kept('I1', { operationType: 'gameRemoval', status: 'failed' }));
    browser.state = settleBulkCards(browser.state, browser.localCards).next;
    browser.push({
      ...kept('I1', { operationType: 'gameRemoval', status: 'failed' }),
      closed: true
    });
    let settled = settleBulkCards(browser.state, browser.localCards);
    browser.state = settled.next;
    assert.deepEqual(settled.release, [], 'a running batch never leaves');

    browser.localCards = [
      bulkRemovalCard({ itemOperationIds: ['I1', 'I2'], failedWithoutRun }, { status: 'failed' })
    ];
    settled = settleBulkCards(browser.state, browser.localCards);
    assert.deepEqual(
      settled.release,
      failedWithoutRun ? [] : ['bulk'],
      failedWithoutRun
        ? 'a failure that never reached the server keeps the card'
        : 'the card leaves when it turns terminal'
    );
  }

  const neverKept = new Browser({
    localCards: [bulkRemovalCard({ itemOperationIds: ['I1'] }, { status: 'failed' })]
  });
  assert.deepEqual(settleBulkCards(neverKept.state, neverKept.localCards).release, []);
});

test('an item promoted onto a removal that was already running stays folded', () => {
  const browser = new Browser({ localCards: [] });
  browser.push(row('R', { operationType: 'gameRemoval' }));
  browser.localCards = [bulkRemovalCard({ currentOperationId: 'I3', itemOperationIds: ['I3'] })];
  browser.push(row('I3', { operationType: 'gameRemoval', status: 'waiting' }));
  browser.push(
    row('I3', { operationType: 'gameRemoval', status: 'completed', nextOperationId: 'R' })
  );
  assert.deepEqual(browser.drawn(), ['bulk:bulk_removal:running']);
  assert.deepEqual(browser.state.entries.get('R').aliases, ['I3', 'R']);
});

// ── Scheduled prefill [17] ──────────────────────────────────────────────────

test('scheduled prefill draws one card per platform and nothing for the run as a whole', () => {
  const browser = new Browser();
  browser.push(row('P1', prefillRunFields));
  browser.push(row('P2', { ...prefillRunFields, serviceId: 'Epic', scheduleId: 'S2' }));
  // A run-level event names the container, which has no row.
  browser.detail('RUN', { message: 'Scheduled prefill started' });
  assert.deepEqual(browser.drawn(), [
    'P1:scheduled_prefill:running',
    'P2:scheduled_prefill:running'
  ]);
});

test('a prefill card carries its platform and schedule from its row alone', () => {
  const live = new Browser();
  live.push(row('P1', prefillRunFields));
  live.push(row('P2', { ...prefillRunFields, status: 'waiting' }));
  live.push(row('P3', { ...prefillRunFields, visibility: 'hidden' }));
  for (const id of ['P1', 'P2']) {
    assert.equal(live.card(id).details.service, 'Steam');
    assert.equal(live.card(id).details.scheduleId, 'S1');
  }
  const hidden = deriveRuns(live.state).find((run) => run.id === 'P3');
  assert.equal(hidden.details.service, 'Steam', 'a Hidden run in `runs` has it too');

  const reloaded = new Browser();
  reloaded.snapshot([
    kept('P4', {
      operationType: 'scheduledPrefill',
      status: 'failed',
      serviceId: 'Epic',
      scheduleId: 'S9'
    })
  ]);
  assert.equal(reloaded.card('P4').details.service, 'Epic');
  assert.equal(reloaded.card('P4').details.scheduleId, 'S9');
});

test('prefill events keep their daemon order, and a new series waits for the run status', () => {
  const browser = new Browser();
  browser.push(row('P1', prefillRunFields));
  const versioned = (series, sequence, message, daemonInstanceId = 'D1') => ({
    message,
    details: { eventEpoch: series, eventSequence: sequence, daemonInstanceId }
  });
  browser.detail('P1', versioned('E1', 2, 'two'));
  browser.detail('P1', versioned('E1', 1, 'one, late'));
  assert.equal(browser.card('P1').message, 'two', 'an older sequence is dropped');
  browser.detail('P1', { message: 'unversioned' });
  assert.equal(browser.card('P1').message, 'two', 'an unversioned event after a versioned one');
  browser.detail('P1', versioned('E1', 3, 'other daemon', 'D2'));
  assert.equal(browser.card('P1').message, 'two', 'another daemon instance');

  browser.detail('P1', versioned('E2', 5, 'new series'));
  assert.equal(browser.card('P1').message, 'two', 'a new series is held');
  assert.equal(browser.state.entries.get('P1').stream.pending.series, 'E2');

  const entry = browser.state.entries.get('P1');
  browser.state = applyDetail(
    browser.state,
    'P1',
    () => versioned('E2', 4, 'run status'),
    'recovery',
    {
      requestSeq: 50,
      generation: browser.state.generation,
      detailRevision: entry.detailRevision
    }
  );
  const adopted = browser.state.entries.get('P1').stream;
  assert.equal(adopted.series, 'E2');
  assert.ok(adopted.retired.has('E1'));
  assert.equal(adopted.sequence, 5);
  assert.equal(
    browser.card('P1').message,
    'new series',
    'the held event of the adopted series applies'
  );
  browser.detail('P1', versioned('E1', 9, 'retired series'));
  assert.equal(browser.card('P1').message, 'new series');
});

test('a live service or log removal carries its service, so its page button stays busy', () => {
  const browser = new Browser();
  const dispatchDetail = (operationId, build, source) => {
    browser.state = applyDetail(browser.state, operationId, build, source, { requestSeq: 0 });
  };
  const entryFor = (type) => notificationEntries.find((entry) => entry.type === type);
  const serviceOf = (id) => deriveRuns(browser.state).find((run) => run.id === id).details.service;

  const serviceRemoval = entryFor('service_removal');
  browser.push(row('SR', { operationType: 'serviceRemoval', name: 'Service Removal' }));
  buildStartedHandler(
    serviceRemoval.started,
    dispatchDetail
  )({
    operationId: 'SR',
    serviceName: 'steam',
    stageKey: 'signalr.serviceRemove.starting.default',
    message: 'Starting removal of steam'
  });
  assert.equal(serviceOf('SR'), 'steam', 'set by the Started event, with no seed card');
  buildProgressHandler(
    serviceRemoval,
    serviceRemoval.progress,
    dispatchDetail
  )({
    operationId: 'SR',
    serviceName: 'steam',
    stageKey: 'signalr.serviceRemove.progress',
    percentComplete: 40
  });
  assert.equal(serviceOf('SR'), 'steam');

  // The log removal Started event names its service only inside its stage context.
  const logRemoval = entryFor('log_removal');
  browser.push(row('LR', { operationType: 'logRemoval', name: 'Log Removal' }));
  buildStartedHandler(
    logRemoval.started,
    dispatchDetail
  )({
    operationId: 'LR',
    stageKey: 'signalr.logRemoval.starting.default',
    context: { service: 'epic' }
  });
  assert.equal(serviceOf('LR'), 'epic');
  buildProgressHandler(
    logRemoval,
    logRemoval.progress,
    dispatchDetail
  )({
    operationId: 'LR',
    status: 'running',
    service: 'epic',
    stageKey: 'signalr.logRemoval.removing',
    context: { service: 'epic' },
    percentComplete: 30
  });
  assert.equal(serviceOf('LR'), 'epic');
});

// ── Reload, reconnect, tab return (criteria 12, 13) ─────────────────────────

test('after a reload the bar shows exactly the runs the server has, each once', () => {
  const browser = new Browser();
  const waiting = row('W', {
    operationType: 'gameRemoval',
    name: 'Game Removal',
    status: 'waiting',
    blockedByName: 'Eviction Scan'
  });
  const running = row('R', { percentComplete: 40 });
  const result = browser.snapshot([waiting, running]);
  assert.deepEqual(browser.drawn(), ['W:game_removal:waiting', 'R:eviction_scan:running']);
  assert.equal(browser.card('R').progress, 40);
  assert.equal(browser.card('W').message, 'Game Removal is waiting for Eviction Scan');
  assert.deepEqual(result.recover, ['W', 'R'], 'both still need their event detail');

  browser.snapshot([waiting, running]);
  browser.reconnect();
  browser.snapshot([waiting, running]);
  assert.deepEqual(browser.drawn(), ['W:game_removal:waiting', 'R:eviction_scan:running']);

  // A finished run the user closed does not come back from a replayed row.
  browser.push(row('R', { status: 'completed' }));
  browser.fade();
  browser.snapshot([waiting, { ...running, status: 'completed', revision: nextRunRevision() }]);
  browser.fade();
  assert.deepEqual(browser.drawn(), ['W:game_removal:waiting']);
});

test('a waiting card names its blocker, follows a new one, and still names it after a reload', () => {
  const browser = new Browser();
  const fields = { operationType: 'gameRemoval', name: 'Game Removal', status: 'waiting' };
  browser.push(row('W', { ...fields, blockedByName: 'Eviction Scan' }));
  assert.equal(browser.card('W').message, 'Game Removal is waiting for Eviction Scan');
  const renamed = row('W', { ...fields, blockedByName: 'Cache Clear' });
  browser.push(renamed);
  assert.equal(browser.card('W').message, 'Game Removal is waiting for Cache Clear');

  const reloaded = new Browser();
  reloaded.snapshot([renamed]);
  assert.equal(reloaded.card('W').message, 'Game Removal is waiting for Cache Clear');
});

test('detail recovery fills a card seen only from its row, and an unknown id changes nothing', async () => {
  const browser = new Browser();
  browser.snapshot([row('R', { percentComplete: 10 })]);
  const answers = {
    '/api/stats/eviction/scan/status': {
      isProcessing: true,
      operationId: 'R',
      stageKey: 'signalr.evictionScan.progress',
      context: { totalProcessed: 5, totalEstimate: 10 },
      percentComplete: 50
    }
  };
  const fetchWithAuth = async (url) => ({
    ok: true,
    status: 200,
    json: async () => answers[url]
  });
  const recover = createRecoveryRunner(fetchWithAuth);
  const applied = [];
  const complete = await recover(new Set(['eviction_scan']), (type, operationId, detail) => {
    applied.push([type, operationId]);
    browser.state = applyDetail(browser.state, operationId, () => detail, 'recovery', {
      requestSeq: 1,
      generation: browser.state.generation,
      detailRevision: 0
    });
  });
  assert.equal(complete, true);
  assert.deepEqual(applied, [['eviction_scan', 'R']]);
  assert.equal(browser.card('R').message, 'signalr.evictionScan.progress');
  assert.equal(browser.card('R').progress, 50);

  const before = browser.state;
  browser.state = applyDetail(browser.state, 'UNKNOWN', () => ({ message: 'x' }), 'recovery', {
    requestSeq: 2,
    generation: browser.state.generation,
    detailRevision: 0
  });
  assert.equal(browser.state, before);

  const failing = createRecoveryRunner(async () => ({ ok: false, status: 500 }));
  assert.equal(await failing(new Set(['eviction_scan']), () => undefined), false);
});

/**
 * A reload mid-removal: the run list, then detail recovery, and no event since. Returns the
 * recovered card's details and the `runs` the busy hook reads.
 */
const reloadRemoval = async (id, operationType, removals) => {
  const browser = new Browser();
  browser.snapshot([row(id, { operationType, name: 'Removal' })]);
  const recover = createRecoveryRunner(async (url) => {
    assert.equal(url, '/api/cache/removals/active');
    return { ok: true, status: 200, json: async () => ({ isProcessing: true, ...removals }) };
  });
  const type = operationType === 'gameRemoval' ? 'game_removal' : 'eviction_removal';
  const complete = await recover(new Set([type]), (_, operationId, detail) => {
    browser.state = applyDetail(browser.state, operationId, () => detail, 'recovery', {
      requestSeq: 1,
      generation: browser.state.generation,
      detailRevision: 0
    });
  });
  assert.equal(complete, true);
  return { details: browser.card(id).details, runs: deriveRuns(browser.state) };
};

test('a game removal recovered after a reload is still the only game shown busy', async () => {
  const named = await reloadRemoval('G', 'gameRemoval', {
    gameRemovals: [
      { operationId: 'G', gameName: 'Diablo IV', entityKind: 'named', service: 'xboxlive' }
    ]
  });
  assert.equal(named.details.service, 'xboxlive');
  assert.equal(named.details.gameName, 'Diablo IV');
  for (const [identifier, busy] of [
    [{ kind: 'namedGame', service: 'xboxlive', gameName: 'Diablo IV' }, true],
    [{ kind: 'namedGame', service: 'blizzard', gameName: 'Diablo IV' }, false],
    [{ kind: 'service', service: 'xboxlive' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, named.runs), busy, JSON.stringify(identifier));

  // An Epic game with no Epic id: the server sends its name in the Epic id's place.
  const epic = await reloadRemoval('E', 'gameRemoval', {
    gameRemovals: [
      {
        operationId: 'E',
        entityKind: 'epic',
        epicAppId: 'Fortnite',
        gameName: 'Fortnite',
        service: 'epicgames'
      }
    ]
  });
  assert.equal(epic.details.service, 'epicgames');
  for (const [identifier, busy] of [
    [{ kind: 'epicGame', gameName: 'Fortnite' }, true],
    [{ kind: 'namedGame', service: 'xboxlive', gameName: 'Fortnite' }, false],
    [{ kind: 'service', service: 'epicgames' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, epic.runs), busy, JSON.stringify(identifier));
});

test('an evicted-data removal recovered after a reload marks only its game busy', async () => {
  const named = await reloadRemoval('V', 'evictionRemoval', {
    evictionRemovals: [{ operationId: 'V', scope: 'named', key: 'blizzard', gameName: 'Diablo IV' }]
  });
  assert.equal(named.details.service, 'blizzard');
  for (const [identifier, busy] of [
    [{ kind: 'namedGame', service: 'blizzard', gameName: 'Diablo IV' }, true],
    [{ kind: 'namedGame', service: 'xboxlive', gameName: 'Diablo IV' }, false],
    [{ kind: 'service', service: 'blizzard' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, named.runs), busy, JSON.stringify(identifier));

  const epic = await reloadRemoval('V2', 'evictionRemoval', {
    evictionRemovals: [{ operationId: 'V2', scope: 'epic', key: 'abc', gameName: 'Fortnite' }]
  });
  assert.equal(epic.details.service, 'epicgames');
  for (const [identifier, busy] of [
    [{ kind: 'epicGame', epicAppId: 'abc' }, true],
    [{ kind: 'epicGame', gameName: 'Fortnite' }, true],
    [{ kind: 'namedGame', service: 'xboxlive', gameName: 'Fortnite' }, false],
    [{ kind: 'service', service: 'epicgames' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, epic.runs), busy, JSON.stringify(identifier));

  const service = await reloadRemoval('V3', 'evictionRemoval', {
    evictionRemovals: [{ operationId: 'V3', scope: 'service', key: 'blizzard' }]
  });
  assert.equal(service.details.service, 'blizzard');
  for (const [identifier, busy] of [
    [{ kind: 'service', service: 'blizzard' }, true],
    [{ kind: 'namedGame', service: 'blizzard', gameName: 'Diablo IV' }, false]
  ])
    assert.equal(await entityBusyFor(identifier, service.runs), busy, JSON.stringify(identifier));
});

// ── Store merge rules, one case per letter a-o [45] ─────────────────────────

test('(a) a row not newer than the last accepted one is ignored, also after the card left', () => {
  const browser = new Browser();
  const first = row('R', { percentComplete: 10 });
  browser.push(first);
  const second = row('R', { percentComplete: 20 });
  browser.push(second);
  browser.push({ ...first, percentComplete: 90 });
  assert.equal(browser.card('R').progress, 20, 'an older row is ignored');

  browser.push(row('R', { status: 'completed' }));
  browser.fade();
  browser.push({ ...second, revision: second.revision });
  browser.push(row('R', { status: 'completed' }));
  assert.deepEqual(browser.drawn(), [], 'a delayed row never brings the card back');

  // A dismissed failure stays dismissed.
  const failure = kept('F', { status: 'failed' });
  browser.push(failure);
  browser.state = removeRuns(browser.state, ['F']);
  browser.push({ ...failure, revision: nextRunRevision() });
  assert.deepEqual(browser.drawn(), []);

  // A pushed row for a run the newest snapshot did not hold is a late copy of a reaped run.
  const late = row('LATE');
  browser.snapshot([], nextRunRevision() + 5);
  browser.push(late);
  assert.deepEqual(browser.drawn(), []);
});

test('(a) an ended run is forgotten only after a request started after its first absence', () => {
  const browser = new Browser();
  browser.push(row('R'));
  const end = row('R', { status: 'completed' });
  browser.push(end);
  browser.fade();
  browser.snapshot([]);
  assert.ok(browser.state.ended.has('R'), 'one absence is not enough');
  browser.snapshot([]);
  assert.ok(!browser.state.ended.has('R'));
  browser.push(end);
  assert.deepEqual(browser.drawn(), [], 'its late row is below the snapshot boundary');
});

test('(c, d) rows after a reconnect win over that reconnect snapshot', () => {
  const browser = new Browser();
  browser.push(row('R', { percentComplete: 10 }));
  browser.reconnect();
  const pending = browser.request();
  const captured = row('R', { percentComplete: 20 });
  const fresh = row('R', { percentComplete: 60 });
  browser.push(fresh);
  const newer = row('N');
  pending([captured], captured.revision);
  assert.equal(browser.card('R').progress, 60, 'the snapshot row is older than the push');

  browser.push(newer);
  const another = browser.request();
  another([fresh], fresh.revision);
  assert.deepEqual(
    browser.drawn(),
    ['R:eviction_scan:running', 'N:eviction_scan:running'],
    'a push newer than the snapshot capture survives its absence'
  );
});

test('(e, n) a snapshot refreshes the percent a push never carries', () => {
  const browser = new Browser();
  const first = row('R', { percentComplete: 10, message: 'signalr.evictionScan.scanning' });
  browser.push(first);
  browser.snapshot([{ ...first, percentComplete: 60, message: 'signalr.evictionScan.finalizing' }]);
  assert.equal(browser.card('R').progress, 60);
  assert.equal(browser.card('R').message, 'signalr.evictionScan.finalizing');

  browser.detail('R', { message: 'Scanning 3 of 10', progress: 30 });
  assert.equal(browser.card('R').progress, 30);
  browser.snapshot([{ ...first, percentComplete: 70 }]);
  assert.equal(browser.card('R').progress, 70, 'no event overtook this snapshot');
  assert.equal(browser.card('R').message, 'Scanning 3 of 10', 'the event text stays');

  const pending = browser.request();
  browser.detail('R', { message: 'Scanning 8 of 10', progress: 80 });
  pending([{ ...first, percentComplete: 75 }], first.revision);
  assert.equal(browser.card('R').progress, 80, 'an event after the request started is newer');
});

test('(f) a malformed row or snapshot is dropped and logged, never drawn', () => {
  const errors = [];
  const original = console.error;
  console.error = (...args) => errors.push(args[0]);
  try {
    const good = row('R');
    assert.equal(readOperationRun({ ...good, status: 7 }), null);
    const { operationId, ...missingId } = good;
    assert.equal(operationId, 'R');
    assert.equal(readOperationRun(missingId), null);
    assert.equal(readOperationRun({ ...good, startedAt: 'not a date' }), null);
    assert.equal(readOperationRun({ ...good, revision: 0 }), null);
    assert.equal(readOperationRun([good]), null);
    assert.deepEqual(readOperationRun(good), good);
    assert.equal(
      readOperationRunsSnapshot({ runs: [good, { ...good, visibility: 'loud' }], revision: 4 }),
      null
    );
    assert.equal(readOperationRunsSnapshot({ runs: {}, revision: 4 }), null);
    assert.deepEqual(readOperationRunsSnapshot({ runs: [good], revision: 4 }), {
      runs: [good],
      revision: 4
    });
    assert.ok(errors.includes('[notifications] dropped malformed run row'));
    assert.ok(errors.includes('[notifications] dropped malformed runs snapshot'));
  } finally {
    console.error = original;
  }
});

test('(j) a completion after the terminal row fills the text, not the status or how long it stays', () => {
  const browser = new Browser();
  browser.push(row('R'));
  browser.push(kept('R', { status: 'failed', error: 'Operation failed' }));
  browser.detail('R', { message: 'Scanning' }, 'event');
  assert.equal(browser.card('R').message, 'signalr.evictionScan.scanning');
  browser.detail(
    'R',
    { message: 'Scan failed: disk gone', error: 'Scan failed: disk gone' },
    'completion'
  );
  const card = browser.card('R');
  assert.equal(card.status, 'failed');
  assert.equal(card.message, 'Scan failed: disk gone');
  assert.equal(card.error, 'Scan failed: disk gone');
  assert.equal(browser.state.entries.get('R').retained, true);
});

test('(k) detail from before a reconnect is recovered after it, and a stale recovery changes nothing', () => {
  const browser = new Browser();
  browser.push(row('R'));
  browser.detail('R', { message: 'Scanning 3 of 10', progress: 30 });
  const staleGeneration = browser.state.generation;
  browser.reconnect();
  const result = browser.snapshot([row('R')]);
  assert.deepEqual(result.recover, ['R']);
  assert.equal(browser.card('R').message, 'signalr.evictionScan.scanning');

  const recoverWith = (requestSeq, generation, message) => {
    browser.state = applyDetail(browser.state, 'R', () => ({ message }), 'recovery', {
      requestSeq,
      generation,
      detailRevision: 0
    });
  };
  recoverWith(10, staleGeneration, 'from before the reconnect');
  assert.equal(browser.card('R').message, 'signalr.evictionScan.scanning');
  recoverWith(12, browser.state.generation, 'newer recovery');
  recoverWith(11, browser.state.generation, 'older recovery');
  assert.equal(browser.card('R').message, 'newer recovery');
});

test('(l) a handoff whose successor already ended resolves the waiting card at once', () => {
  const browser = new Browser();
  browser.push(row('N'));
  browser.push(row('N', { status: 'completed' }));
  browser.fade();
  browser.push(row('W', { status: 'waiting' }));
  browser.push(row('W', { status: 'completed', nextOperationId: 'N' }));
  assert.deepEqual(browser.drawn(), []);
  assert.deepEqual(locateRun(browser.state, 'W').terminal, {
    operationId: 'N',
    status: 'completed'
  });

  const unsure = new Browser();
  unsure.push(row('W2', { status: 'waiting' }));
  unsure.push(row('W2', { status: 'completed', nextOperationId: 'GONE' }));
  assert.deepEqual(unsure.drawn(), ['W2:eviction_scan:waiting']);
  unsure.snapshot([]);
  assert.deepEqual(unsure.drawn(), []);
  assert.deepEqual(locateRun(unsure.state, 'W2').terminal, { operationId: 'GONE', status: 'gone' });
});

test('(m) two overlapping snapshots that both miss an ended run keep its record', () => {
  const browser = new Browser();
  browser.push(row('R'));
  const end = row('R', { status: 'completed' });
  browser.push(end);
  browser.fade();
  const first = browser.request();
  const second = browser.request();
  first([]);
  second([]);
  assert.ok(browser.state.ended.has('R'));
  browser.push({ ...end, revision: nextRunRevision() });
  assert.deepEqual(browser.drawn(), []);
});

test('handoffs converge: two predecessors merge into one card drawn as its row says', () => {
  const browser = new Browser();
  browser.push(row('CW1', { status: 'waiting' }));
  browser.push(row('CW2', { status: 'waiting', visibility: 'background' }));
  browser.push(row('CN', { visibility: 'background' }));
  browser.push(row('CW1', { status: 'completed', nextOperationId: 'CN' }));
  browser.push(row('CW2', { status: 'completed', nextOperationId: 'CN' }));
  assert.deepEqual(
    browser.drawn(),
    ['CW1:eviction_scan:running:row'],
    'the merged card is as visible as the successor row, never more'
  );
  // The server raises the successor for the full card it absorbed.
  browser.push(row('CN'));
  assert.deepEqual(browser.drawn(), ['CW1:eviction_scan:running']);
  assert.deepEqual(browser.state.entries.get('CN').aliases, ['CW2', 'CW1', 'CN']);
  assert.deepEqual(browser.merged, [
    { from: 'CW1', to: 'CN' },
    { from: 'CW2', to: 'CN' }
  ]);
});

test('a handoff card is drawn as visible as the last row says, never a value of its own', () => {
  // A Manual-only schedule: the run someone started waits as a full card and hands its work to
  // the scheduled run, a background row until the server raises it.
  const promoted = new Browser();
  promoted.push(row('W', { status: 'waiting' }));
  promoted.push(row('N', { visibility: 'background', previousOperationId: 'W' }));
  assert.deepEqual(promoted.drawn(), ['W:eviction_scan:running:row']);
  promoted.push(row('N'));
  assert.deepEqual(promoted.drawn(), ['W:eviction_scan:running']);
  promoted.push(row('N', { visibility: 'background' }));
  assert.deepEqual(promoted.drawn(), ['W:eviction_scan:running:row']);

  // The same work continued on the waiting id, and a successor seen before the handoff row.
  const continued = new Browser();
  continued.push(row('C', { status: 'waiting' }));
  continued.push(row('C', { visibility: 'background' }));
  assert.deepEqual(continued.drawn(), ['C:eviction_scan:running:row']);

  const linked = new Browser();
  linked.push(row('LW', { status: 'waiting' }));
  linked.push(row('LN', { visibility: 'background' }));
  linked.push(row('LW', { status: 'completed', nextOperationId: 'LN' }));
  assert.deepEqual(linked.drawn(), ['LW:eviction_scan:running:row']);
  linked.push(row('LN'));
  assert.deepEqual(linked.drawn(), ['LW:eviction_scan:running']);
});

test('a waiter finds how a run ended, following every merge', () => {
  const browser = new Browser();
  browser.push(row('W', { status: 'waiting' }));
  browser.push(row('N', { previousOperationId: 'W' }));
  assert.deepEqual(locateRun(browser.state, 'W'), {
    operationId: 'N',
    known: true,
    terminal: undefined
  });
  browser.push(row('N', { status: 'failed', error: 'Boom', retained: true }));
  assert.deepEqual(locateRun(browser.state, 'W').terminal, {
    operationId: 'N',
    status: 'failed',
    error: 'Boom'
  });
  assert.deepEqual(browser.ended, [{ operationId: 'N', status: 'failed', error: 'Boom' }]);
  assert.deepEqual(locateRun(browser.state, 'NEVER'), { operationId: 'NEVER', known: false });
});

// ── The provider's snapshot request, lifted from NotificationsContext.tsx ───

const CONTEXT_PATH = 'src/contexts/notifications/NotificationsContext.tsx';

/** A promise the test settles by hand. */
const deferred = () => {
  let resolve;
  let reject;
  const promise = new Promise((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
};

const flush = async () => {
  for (let i = 0; i < 10; i++) await Promise.resolve();
};

/** The shipped request and push handlers bound to one browser's refs. */
const liftRunHandlers = ({ invoke, fetchWithAuth, sessionId = null }) => {
  const refs = {
    storeRef: { current: createRunStoreState() },
    localRef: { current: [] },
    requestSeqRef: { current: 0 },
    appliedSeqRef: { current: 0 },
    resyncPendingRef: { current: false },
    sessionIdRef: { current: sessionId },
    snapshotRetryRef: { current: undefined },
    isAdminRef: { current: true },
    isConnectedRef: { current: true }
  };
  const applied = [];
  // The retry timers the request scheduled and cleared; none of them runs unless a test calls it.
  const retries = [];
  const cleared = [];
  const commitApply = (result) => {
    refs.storeRef.current = result.next;
    applied.push(result);
  };
  const requestSnapshot = bindLifted(
    liftHookCallback(CONTEXT_PATH, 'useCallback', 'JoinAuthenticatedGroupAsync'),
    {
      ...refs,
      invoke,
      fetchWithAuth,
      readOperationRunsSnapshot,
      applySnapshot,
      shouldAutoDismiss: () => true,
      commitApply,
      recoverDetails: async () => undefined,
      RUN_LIST_RETRY_DELAY_MS: 5000,
      setTimeout: (callback, delay) => {
        retries.push({ callback, delay });
        return retries.length;
      },
      clearTimeout: (id) => cleared.push(id),
      // The retry calls the request itself.
      requestSnapshot: () => requestSnapshot()
    }
  );
  const handleRun = bindLifted(
    liftHookCallback(CONTEXT_PATH, 'useCallback', 'readOperationRun(value)'),
    {
      ...refs,
      readOperationRun,
      applyRun,
      requestSnapshot,
      shouldAutoDismiss: () => true,
      commitApply
    }
  );
  return { ...refs, applied, retries, cleared, requestSnapshot, handleRun };
};

const response = (runs, snapshotRevision = nextRunRevision()) => ({
  ok: true,
  status: 200,
  json: async () => ({ runs, revision: snapshotRevision })
});

test('(i) every snapshot request waits for the admin group join, resolved or rejected', async () => {
  for (const outcome of ['resolve', 'reject']) {
    const join = deferred();
    const fetched = [];
    const handlers = liftRunHandlers({
      invoke: () => join.promise,
      fetchWithAuth: async (url) => {
        fetched.push(url);
        return response([row('R')]);
      }
    });
    const request = handlers.requestSnapshot();
    await flush();
    assert.deepEqual(fetched, [], `no fetch before the join settles (${outcome})`);
    if (outcome === 'resolve') join.resolve();
    else join.reject(new Error('connection dropped'));
    await request;
    assert.deepEqual(fetched, ['/api/operations/runs'], outcome);
    assert.equal(handlers.applied.length, 1);
  }

  // Mount, connect and tab return all go through this one function.
  const source = (await import('node:fs')).readFileSync(
    new URL(`../${CONTEXT_PATH}`, import.meta.url),
    'utf8'
  );
  assert.equal(source.match(/JoinAuthenticatedGroupAsync/g).length, 1);
  assert.match(source, /if \(isAdmin\) void requestSnapshot\(\);/);
  assert.match(source, /if \(isAdminRef\.current\) void requestSnapshot\(\);/);
  assert.match(
    source,
    /document\.visibilityState === 'visible' && isAdminRef\.current\) void requestSnapshot\(\)/
  );
});

test('(b) a response older than one applied, or from before a reconnect, is dropped whole', async () => {
  const answers = [deferred(), deferred()];
  let call = 0;
  const handlers = liftRunHandlers({
    invoke: async () => undefined,
    fetchWithAuth: () => answers[call++].promise
  });
  const older = handlers.requestSnapshot();
  const newer = handlers.requestSnapshot();
  await flush();
  answers[1].resolve(response([row('NEW')]));
  await newer;
  answers[0].resolve(response([row('OLD')]));
  await older;
  assert.deepEqual([...handlers.storeRef.current.entries.keys()], ['NEW']);

  const across = deferred();
  const reconnecting = liftRunHandlers({
    invoke: async () => undefined,
    fetchWithAuth: () => across.promise
  });
  const pending = reconnecting.requestSnapshot();
  await flush();
  reconnecting.storeRef.current = nextGeneration(reconnecting.storeRef.current);
  across.resolve(response([row('R')]));
  await pending;
  assert.equal(reconnecting.applied.length, 0);
});

test('a run list requested before a session change is refused whole', async () => {
  const answer = deferred();
  const handlers = liftRunHandlers({
    invoke: async () => undefined,
    fetchWithAuth: () => answer.promise,
    sessionId: 'session-a'
  });
  handlers.handleRun(row('L', signIn));
  assert.ok(handlers.storeRef.current.entries.has('L'), 'the owner browser drew it');
  const pending = handlers.requestSnapshot();
  await flush();
  handlers.storeRef.current = changeSession(handlers.storeRef.current, 'session-b');
  handlers.sessionIdRef.current = 'session-b';
  assert.equal(handlers.storeRef.current.entries.size, 0, 'gone before any answer');
  answer.resolve(response([row('L', signIn), row('R')]));
  await pending;
  assert.equal(handlers.applied.length, 1, 'only the push was applied, never the late list');
  assert.equal(handlers.storeRef.current.entries.size, 0);
});

test('the session effect ends the earlier session cards first, then asks for the run list', () => {
  const calls = [];
  const effect = (sessionBefore, sessionId, isAdmin) => {
    const refs = {
      storeRef: {
        current: pushRun(
          modules,
          createRunStoreState(),
          row('R', { operationType: 'gameDetection' })
        )
      },
      sessionBeforeRef: { current: sessionBefore },
      isAdminRef: { current: isAdmin }
    };
    bindLifted(liftHookCallback(CONTEXT_PATH, 'useEffect', 'changeSession('), {
      ...refs,
      sessionId,
      changeSession: (state, id) => {
        calls.push(['changeSession', id]);
        return changeSession(state, id);
      },
      commit: () => calls.push(['commit']),
      requestSnapshot: async () => calls.push(['requestSnapshot'])
    })();
    return refs;
  };

  effect('session-a', 'session-a', true);
  assert.deepEqual(calls, [], 'the first render and an unchanged session do nothing');
  const refs = effect('session-a', 'session-b', true);
  assert.deepEqual(calls, [['changeSession', 'session-b'], ['commit'], ['requestSnapshot']]);
  assert.equal(refs.sessionBeforeRef.current, 'session-b');
  calls.length = 0;
  effect('session-a', null, false);
  assert.deepEqual(calls, [['changeSession', null], ['commit']], 'a sign-out asks for nothing');
});

// ── The provider's exit for a run card that leaves on its own ───────────────

/**
 * The shipped `fadeLeavingRuns`, Keep Notifications Visible handler and unmount cleanup bound to
 * one browser's store, recording each fade started. The browser's `keepSuccessVisible` is the
 * setting.
 */
const liftFadeLeavingRuns = (browser) => {
  const removing = [];
  const listeners = new Map();
  const autoDismissTimersRef = { current: new Map() };
  const storeRef = {
    get current() {
      return browser.state;
    },
    set current(state) {
      browser.state = state;
    }
  };
  const window = {
    dispatchEvent: (event) => removing.push(event.detail.notificationId),
    addEventListener: (name, handler) => listeners.set(name, handler),
    removeEventListener: () => undefined
  };
  const APP_EVENTS = {
    NOTIFICATION_REMOVING: 'removing',
    NOTIFICATION_VISIBILITY_CHANGE: 'visibility-change'
  };
  const shouldAutoDismiss = () => !browser.keepSuccessVisible;
  const lift = (hook, marker, bindings) =>
    bindLifted(liftHookCallback(CONTEXT_PATH, hook, marker), bindings);
  const fadeLeavingRuns = lift('useCallback', 'turned on during the hold keeps the card', {
    storeRef,
    autoDismissTimersRef,
    fadingRef: { current: new Set() },
    window,
    APP_EVENTS,
    AUTO_DISMISS_DELAY_MS: modules.AUTO_DISMISS_DELAY_MS,
    NOTIFICATION_ANIMATION_DURATION_MS: modules.NOTIFICATION_ANIMATION_DURATION_MS,
    removeRuns,
    commit: () => undefined,
    shouldAutoDismiss
  });
  lift('useEffect', 'handleNotificationVisibilityChange', {
    shouldAutoDismiss,
    storeRef,
    autoDismissTimersRef,
    releaseKeptSuccess,
    localRef: { current: [] },
    isTerminalNotificationStatus: modules.isTerminalNotificationStatus,
    scheduleAutoDismiss: () => undefined,
    fadeLeavingRuns,
    commit: () => undefined,
    window,
    APP_EVENTS
  })();
  const unmount = lift('useEffect', 'autoDismissTimersRef.current.clear()', {
    autoDismissTimersRef,
    snapshotRetryRef: { current: undefined }
  })();
  return {
    fadeLeavingRuns,
    removing,
    /** The setting changed on this screen: the provider's handler runs as it does in the app. */
    setKeepVisible: (on) => {
      browser.keepSuccessVisible = on;
      listeners.get(APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE)();
    },
    unmount
  };
};

test('a finished run card that leaves on its own stays for the popup time, then fades', () => {
  const hold = modules.AUTO_DISMISS_DELAY_MS;
  const fade = modules.NOTIFICATION_ANIMATION_DURATION_MS;
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    for (const status of ['completed', 'cancelled']) {
      for (const keepSuccessVisible of [false, true]) {
        const label = `${status} keep=${keepSuccessVisible}`;
        const browser = new Browser({ keepSuccessVisible });
        const exit = liftFadeLeavingRuns(browser);
        browser.push(row('R'));
        browser.push(row('R', { status }));
        exit.fadeLeavingRuns();
        mock.timers.tick(hold - 1);
        assert.deepEqual(browser.drawn(), [`R:eviction_scan:${status}`], `${label} at ${hold - 1}`);
        assert.deepEqual(exit.removing, [], `${label}: no fade yet`);
        // Two ticks: a timer the hold's callback starts does not fire inside the same tick.
        mock.timers.tick(1);
        mock.timers.tick(fade);
        assert.deepEqual(
          browser.drawn(),
          keepSuccessVisible ? [`R:eviction_scan:${status}`] : [],
          `${label} after the hold and the fade`
        );
      }
    }
  } finally {
    mock.timers.reset();
  }
});

test('a leaving run card taken over by a newer run, or closed by hand, is not faded again', () => {
  const hold = modules.AUTO_DISMISS_DELAY_MS;
  const fade = modules.NOTIFICATION_ANIMATION_DURATION_MS;
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    // The next run absorbs the card while it is held: the card lights up again and stays.
    const relit = new Browser();
    const relitExit = liftFadeLeavingRuns(relit);
    relit.push(row('W'));
    relit.push(row('W', { status: 'completed' }));
    relitExit.fadeLeavingRuns();
    mock.timers.tick(1000);
    relit.push(row('N', { previousOperationId: 'W' }));
    relitExit.fadeLeavingRuns();
    mock.timers.tick(hold);
    mock.timers.tick(fade);
    assert.deepEqual(relit.drawn(), ['W:eviction_scan:running']);
    assert.deepEqual(relitExit.removing, []);

    // Taken over, then ended again inside the first hold: the card gets a full hold from its new
    // ending, not what was left of the first one.
    const again = new Browser();
    const againExit = liftFadeLeavingRuns(again);
    again.push(row('A1'));
    again.push(row('A1', { status: 'completed' }));
    againExit.fadeLeavingRuns();
    mock.timers.tick(1000);
    again.push(row('A2', { previousOperationId: 'A1' }));
    againExit.fadeLeavingRuns();
    mock.timers.tick(1000);
    again.push(row('A2', { status: 'completed' }));
    againExit.fadeLeavingRuns();
    mock.timers.tick(hold - 1);
    assert.deepEqual(again.drawn(), ['A1:eviction_scan:completed'], 'held a full hold again');
    assert.deepEqual(againExit.removing, []);
    mock.timers.tick(1);
    mock.timers.tick(fade);
    assert.deepEqual(again.drawn(), []);
    assert.deepEqual(againExit.removing, ['A1']);

    // Closed by hand while held: the bar played its own fade, so none starts here.
    const closed = new Browser();
    const closedExit = liftFadeLeavingRuns(closed);
    closed.push(row('C'));
    closed.push(row('C', { status: 'completed' }));
    closedExit.fadeLeavingRuns();
    mock.timers.tick(1000);
    closed.state = removeRuns(closed.state, ['C']);
    mock.timers.tick(hold);
    mock.timers.tick(fade);
    assert.deepEqual(closedExit.removing, []);
  } finally {
    mock.timers.reset();
  }
});

test('a leaving run card stays when Keep Notifications Visible is turned on during its hold', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const browser = new Browser();
    const exit = liftFadeLeavingRuns(browser);
    browser.push(row('R'));
    browser.push(row('R', { status: 'completed' }));
    exit.fadeLeavingRuns();
    mock.timers.tick(2000);
    browser.keepSuccessVisible = true;
    mock.timers.tick(modules.AUTO_DISMISS_DELAY_MS);
    mock.timers.tick(modules.NOTIFICATION_ANIMATION_DURATION_MS);
    assert.deepEqual(browser.drawn(), ['R:eviction_scan:completed']);
    assert.deepEqual(exit.removing, []);
  } finally {
    mock.timers.reset();
  }
});

test('a card closed on another screen leaves at once, whatever Keep Notifications Visible says', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    for (const keepSuccessVisible of [true, false]) {
      for (const how of ['closed row', 'missing from a snapshot']) {
        const label = `${how}, keep=${keepSuccessVisible}`;
        const browser = new Browser({ keepSuccessVisible });
        const exit = liftFadeLeavingRuns(browser);
        const failure = kept('F', { status: 'failed' });
        browser.push(failure);
        if (how === 'closed row')
          browser.push({ ...failure, closed: true, revision: nextRunRevision() });
        else browser.snapshot([]);
        exit.fadeLeavingRuns();
        mock.timers.tick(1);
        mock.timers.tick(modules.NOTIFICATION_ANIMATION_DURATION_MS);
        assert.deepEqual(browser.drawn(), [], label);
        assert.deepEqual(exit.removing, ['F'], label);
      }
    }
  } finally {
    mock.timers.reset();
  }
});

test('Keep Notifications Visible turned on, then off, gives a leaving card a fresh hold', () => {
  const hold = modules.AUTO_DISMISS_DELAY_MS;
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const browser = new Browser();
    const exit = liftFadeLeavingRuns(browser);
    browser.push(row('R'));
    browser.push(row('R', { status: 'completed' }));
    exit.fadeLeavingRuns();
    mock.timers.tick(1000);
    exit.setKeepVisible(true);
    mock.timers.tick(1000);
    exit.setKeepVisible(false);
    mock.timers.tick(hold - 1);
    assert.deepEqual(
      browser.drawn(),
      ['R:eviction_scan:completed'],
      'a full hold from turning off'
    );
    assert.deepEqual(exit.removing, []);
    mock.timers.tick(1);
    mock.timers.tick(modules.NOTIFICATION_ANIMATION_DURATION_MS);
    assert.deepEqual(browser.drawn(), []);
  } finally {
    mock.timers.reset();
  }
});

test('an older fade never removes the newer run that took over its card', () => {
  const hold = modules.AUTO_DISMISS_DELAY_MS;
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const browser = new Browser();
    const exit = liftFadeLeavingRuns(browser);
    browser.push(row('W'));
    browser.push(row('W', { status: 'completed' }));
    exit.fadeLeavingRuns();
    mock.timers.tick(hold);
    mock.timers.tick(100);
    assert.deepEqual(exit.removing, ['W'], 'the old run is fading');
    browser.push(row('N', { previousOperationId: 'W' }));
    exit.fadeLeavingRuns();
    mock.timers.tick(100);
    browser.push(row('N', { status: 'completed' }));
    exit.fadeLeavingRuns();
    mock.timers.tick(200);
    assert.deepEqual(browser.drawn(), ['W:eviction_scan:completed'], 'the old fade has run out');
    mock.timers.tick(hold - 201);
    assert.deepEqual(
      browser.drawn(),
      ['W:eviction_scan:completed'],
      'a full hold after its ending'
    );
    mock.timers.tick(1);
    mock.timers.tick(modules.NOTIFICATION_ANIMATION_DURATION_MS);
    assert.deepEqual(browser.drawn(), []);
  } finally {
    mock.timers.reset();
  }
});

test('an unmount during a run card fade stops the fade', () => {
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    const browser = new Browser();
    const exit = liftFadeLeavingRuns(browser);
    browser.push(row('R'));
    browser.push(row('R', { status: 'completed' }));
    exit.fadeLeavingRuns();
    mock.timers.tick(modules.AUTO_DISMISS_DELAY_MS);
    mock.timers.tick(100);
    assert.deepEqual(exit.removing, ['R'], 'the fade started');
    exit.unmount();
    mock.timers.tick(modules.NOTIFICATION_ANIMATION_DURATION_MS);
    assert.ok(browser.state.entries.has('R'), 'no timer ran after the unmount');
  } finally {
    mock.timers.reset();
  }
});

test('(h, o) a dropped row or a failed request asks again on the next push', async () => {
  const fetched = [];
  let fail = true;
  const handlers = liftRunHandlers({
    invoke: async () => undefined,
    fetchWithAuth: async (url) => {
      fetched.push(url);
      return fail ? { ok: false, status: 500 } : response([]);
    }
  });
  await handlers.requestSnapshot();
  assert.equal(handlers.resyncPendingRef.current, true);
  fail = false;
  handlers.handleRun(row('R'));
  await flush();
  assert.equal(fetched.length, 2, 'exactly one new snapshot request');
  assert.equal(handlers.resyncPendingRef.current, false);

  const errors = console.error;
  console.error = () => undefined;
  try {
    handlers.handleRun({ operationId: 'R', status: 7 });
  } finally {
    console.error = errors;
  }
  await flush();
  assert.equal(fetched.length, 3, 'a row this browser dropped asks for a snapshot at once');
});

test('(p) a failed run-list read is asked again after 5 s while connected, and ends a run that finished meanwhile', async () => {
  const fetched = [];
  let fail = true;
  const handlers = liftRunHandlers({
    invoke: async () => undefined,
    fetchWithAuth: async (url) => {
      fetched.push(url);
      return fail ? { ok: false, status: 500 } : response([]);
    }
  });
  handlers.handleRun(row('R'));
  await handlers.requestSnapshot();
  assert.deepEqual(
    handlers.retries.map((retry) => retry.delay),
    [5000]
  );

  fail = false;
  handlers.retries[0].callback();
  await flush();
  assert.equal(fetched.length, 2);
  assert.ok(handlers.cleared.includes(1), 'a new request replaces the scheduled retry');
  assert.deepEqual(handlers.applied.at(-1).ended, [{ operationId: 'R', status: 'gone' }]);
  assert.deepEqual(deriveNotifications(handlers.storeRef.current, []), []);

  fail = true;
  await handlers.requestSnapshot();
  assert.equal(handlers.retries.length, 2);
  handlers.isConnectedRef.current = false;
  handlers.retries[1].callback();
  await flush();
  assert.equal(fetched.length, 3, 'no request while the connection is down');
});

test('a handoff to an unknown successor asks for a snapshot once', async () => {
  const fetched = [];
  const handlers = liftRunHandlers({
    invoke: async () => undefined,
    fetchWithAuth: async (url) => {
      fetched.push(url);
      return response([row('N')]);
    }
  });
  handlers.handleRun(row('W', { status: 'waiting' }));
  handlers.handleRun(row('W', { status: 'completed', nextOperationId: 'N' }));
  await flush();
  assert.equal(fetched.length, 1);
  assert.equal(handlers.storeRef.current.entries.get('N').cardId, 'W');
});

// ── The provider's waiter, lifted from NotificationsContext.tsx ─────────────

test('a waiter follows a promotion before it reads a status, and a later 404 releases it', async () => {
  class ApiError extends Error {
    constructor(status) {
      super(`status ${status}`);
      this.status = status;
    }
  }
  const answers = [];
  const storeRef = { current: createRunStoreState() };
  const follow = bindLifted(liftHookCallback(CONTEXT_PATH, 'useMemo', 'probeRun'), {
    storeRef,
    locateRun,
    ApiService: {
      getTrackedOperation: async (id) => {
        const answer = answers.shift();
        assert.equal(id, answer.id);
        if (answer.throws) throw answer.throws;
        return answer.body;
      }
    },
    ApiError,
    isTerminalNotificationStatus: (status) =>
      ['completed', 'failed', 'cancelled', 'skipped'].includes(status),
    endStatus: (status) => status
  })();
  const settled = [];
  const waiter = { target: 'W', probing: false, settle: (end) => settled.push(end) };

  answers.push(
    { id: 'W', body: { id: 'W', status: 'completed', nextOperationId: 'N' } },
    { id: 'N', body: { id: 'N', status: 'running' } }
  );
  follow(waiter, true);
  await flush();
  assert.deepEqual(settled, [], 'a live successor keeps the waiter');
  assert.equal(waiter.target, 'N');

  answers.push({ id: 'N', throws: new ApiError(404) });
  follow(waiter, true);
  await flush();
  assert.deepEqual(settled, [{ operationId: 'N', status: 'gone' }]);

  // A run that ended before anyone waited for it answers with its terminal status.
  const late = { target: 'DONE', probing: false, settle: (end) => settled.push(end) };
  answers.push({ id: 'DONE', body: { id: 'DONE', status: 'failed', error: 'Boom' } });
  follow(late, true);
  await flush();
  assert.deepEqual(settled.at(-1), { operationId: 'DONE', status: 'failed', error: 'Boom' });

  // A run the store already knows is followed from the store, with no request.
  storeRef.current = pushRun(modules, storeRef.current, row('KNOWN', { status: 'waiting' }));
  const known = { target: 'KNOWN', probing: false, settle: (end) => settled.push(end) };
  follow(known, true);
  await flush();
  assert.equal(answers.length, 0);
  assert.equal(known.target, 'KNOWN');
});
