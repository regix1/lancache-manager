import assert from 'node:assert/strict';
import test from 'node:test';
import {
  canAcceptRestSnapshot,
  canAcceptSignalRSnapshot,
  hasImmediateSnapshotChange,
  isDownloadSpeedSnapshot
} from '../src/contexts/SpeedContext/snapshot.ts';
import { bindLifted, liftHookCallback } from './transpile-module.mjs';

const FIRST_SEEN = '2026-09-30T12:00:00.000Z';
const LAST_SEEN = '2026-09-30T12:00:05.000Z';
const ACTIVE_UNTIL = '2026-09-30T12:00:20.000Z';
const MEASURED_UNTIL = '2026-09-30T12:00:07.000Z';

const source = (overrides = {}) => ({
  datasources: ['primary'],
  depotIds: [731],
  firstSeenUtc: FIRST_SEEN,
  lastSeenUtc: LAST_SEEN,
  activeUntilUtc: ACTIVE_UNTIL,
  measuredUntilUtc: MEASURED_UNTIL,
  bytesPerSecond: 100,
  totalBytes: 200,
  requestCount: 2,
  cacheHitBytes: 150,
  cacheMissBytes: 50,
  ...overrides
});

const game = (overrides = {}) => ({
  key: 'steam|10.0.0.1|app:730',
  depotId: 731,
  gameName: 'Counter-Strike 2',
  gameAppId: 730,
  service: 'steam',
  clientIp: '10.0.0.1',
  bytesPerSecond: 100,
  totalBytes: 200,
  requestCount: 2,
  cacheHitBytes: 150,
  cacheMissBytes: 50,
  cacheHitPercent: 75,
  isEvicted: false,
  firstSeenUtc: FIRST_SEEN,
  lastSeenUtc: LAST_SEEN,
  activeUntilUtc: ACTIVE_UNTIL,
  sources: [source()],
  ...overrides
});

const client = (overrides = {}) => ({
  clientIp: '10.0.0.1',
  bytesPerSecond: 100,
  totalBytes: 200,
  activeGames: 1,
  cacheHitBytes: 150,
  cacheMissBytes: 50,
  activeUntilUtc: ACTIVE_UNTIL,
  ...overrides
});

const snapshot = (overrides = {}) => ({
  version: 2,
  streamId: 'manager-a',
  revision: 1,
  timestampUtc: LAST_SEEN,
  isAvailable: true,
  totalBytesPerSecond: 100,
  gameSpeeds: [game()],
  clientSpeeds: [client()],
  windowSeconds: 2,
  entriesInWindow: 2,
  hasActiveDownloads: true,
  ...overrides
});

test('accepts the complete version 2 public shape including a retained zero-rate row', () => {
  assert.equal(isDownloadSpeedSnapshot(snapshot()), true);
  assert.equal(
    isDownloadSpeedSnapshot(
      snapshot({
        revision: 2,
        totalBytesPerSecond: 0,
        entriesInWindow: 0,
        gameSpeeds: [
          game({
            bytesPerSecond: 0,
            totalBytes: 0,
            requestCount: 0,
            cacheHitBytes: 0,
            cacheMissBytes: 0,
            cacheHitPercent: 0,
            sources: [
              source({
                bytesPerSecond: 0,
                totalBytes: 0,
                requestCount: 0,
                cacheHitBytes: 0,
                cacheMissBytes: 0
              })
            ]
          })
        ],
        clientSpeeds: [
          client({
            bytesPerSecond: 0,
            totalBytes: 0,
            cacheHitBytes: 0,
            cacheMissBytes: 0
          })
        ]
      })
    ),
    true
  );
});

test('rejects old or incomplete public snapshots without local defaults', () => {
  assert.equal(isDownloadSpeedSnapshot({ ...snapshot(), version: 1 }), false);
  assert.equal(isDownloadSpeedSnapshot({ ...snapshot(), streamId: '' }), false);
  assert.equal(isDownloadSpeedSnapshot({ ...snapshot(), windowSeconds: 15 }), false);
  assert.equal(
    isDownloadSpeedSnapshot({
      ...snapshot(),
      gameSpeeds: [game({ activeUntilUtc: undefined })]
    }),
    false
  );
  assert.equal(
    isDownloadSpeedSnapshot({
      ...snapshot(),
      gameSpeeds: [game({ sources: [source({ datasources: [] })] })]
    }),
    false
  );
});

test('rejects invalid measurements and unordered activity boundaries', () => {
  assert.equal(isDownloadSpeedSnapshot({ ...snapshot(), totalBytesPerSecond: Number.NaN }), false);
  assert.equal(
    isDownloadSpeedSnapshot({
      ...snapshot(),
      gameSpeeds: [game({ firstSeenUtc: ACTIVE_UNTIL })]
    }),
    false
  );
  assert.equal(
    isDownloadSpeedSnapshot({
      ...snapshot(),
      gameSpeeds: [game({ sources: [source({ measuredUntilUtc: ACTIVE_UNTIL })] })]
    }),
    true
  );
  assert.equal(
    isDownloadSpeedSnapshot({
      ...snapshot(),
      gameSpeeds: [game({ sources: [source({ measuredUntilUtc: '2026-09-30T12:00:21.000Z' })] })]
    }),
    false
  );
});

test('REST alone establishes a stream while SignalR advances only the established stream', () => {
  const first = snapshot();
  const newer = snapshot({ revision: 2 });
  const restarted = snapshot({ streamId: 'manager-b', revision: 1 });
  assert.equal(canAcceptRestSnapshot(null, first), true);
  assert.equal(canAcceptRestSnapshot(first, first), false);
  assert.equal(canAcceptRestSnapshot(first, newer), true);
  assert.equal(canAcceptRestSnapshot(newer, first), false);
  assert.equal(canAcceptRestSnapshot(newer, restarted), true);
  assert.equal(canAcceptSignalRSnapshot(null, first), false);
  assert.equal(canAcceptSignalRSnapshot(first, newer), true);
  assert.equal(canAcceptSignalRSnapshot(first, restarted), false);
});

test('same-count identity replacement and source removal bypass numeric throttling', () => {
  const current = snapshot();
  const replacement = snapshot({
    revision: 2,
    gameSpeeds: [
      game({
        key: 'steam|10.0.0.1|app:440',
        gameName: 'Team Fortress 2',
        gameAppId: 440
      })
    ]
  });
  assert.equal(hasImmediateSnapshotChange(current, replacement), true);

  const twoSources = snapshot({
    gameSpeeds: [
      game({ sources: [source(), source({ datasources: ['secondary'], depotIds: [732] })] })
    ]
  });
  assert.equal(hasImmediateSnapshotChange(twoSources, snapshot({ revision: 2 })), true);
});

test('availability and positive-to-zero measurement changes render immediately', () => {
  const current = snapshot();
  assert.equal(
    hasImmediateSnapshotChange(current, snapshot({ revision: 2, isAvailable: false })),
    true
  );
  assert.equal(
    hasImmediateSnapshotChange(
      current,
      snapshot({
        revision: 2,
        totalBytesPerSecond: 0,
        entriesInWindow: 0,
        gameSpeeds: [
          game({
            bytesPerSecond: 0,
            totalBytes: 0,
            requestCount: 0,
            cacheHitBytes: 0,
            cacheMissBytes: 0,
            cacheHitPercent: 0,
            sources: [
              source({
                bytesPerSecond: 0,
                totalBytes: 0,
                requestCount: 0,
                cacheHitBytes: 0,
                cacheMissBytes: 0
              })
            ]
          })
        ],
        clientSpeeds: [
          client({
            bytesPerSecond: 0,
            totalBytes: 0,
            cacheHitBytes: 0,
            cacheMissBytes: 0
          })
        ]
      })
    ),
    true
  );
});

test('positive numeric-only changes remain eligible for the selected render throttle', () => {
  const current = snapshot();
  const next = snapshot({
    revision: 2,
    timestampUtc: '2026-09-30T12:00:06.000Z',
    totalBytesPerSecond: 200,
    gameSpeeds: [
      game({
        bytesPerSecond: 200,
        totalBytes: 400,
        requestCount: 4,
        cacheHitBytes: 300,
        cacheMissBytes: 100,
        sources: [
          source({
            bytesPerSecond: 200,
            totalBytes: 400,
            requestCount: 4,
            cacheHitBytes: 300,
            cacheMissBytes: 100
          })
        ]
      })
    ],
    clientSpeeds: [
      client({
        bytesPerSecond: 200,
        totalBytes: 400,
        cacheHitBytes: 300,
        cacheMissBytes: 100
      })
    ],
    entriesInWindow: 4
  });
  assert.equal(hasImmediateSnapshotChange(current, next), false);
});

test('the shipped render callback keeps one trailing numeric snapshot and commits the newest', () => {
  const callback = liftHookCallback(
    'src/contexts/SpeedContext/index.tsx',
    'useCallback',
    'hasImmediateSnapshotChange'
  );
  const committed = [];
  const rendered = snapshot();
  const renderedSnapshotRef = { current: rendered };
  const pendingSnapshotRef = { current: null };
  const lastSpeedUpdateRef = { current: Date.now() };
  const throttleTimerRef = { current: null };
  let scheduled = null;
  let timerCount = 0;
  const renderAcceptedSnapshot = bindLifted(callback, {
    renderedSnapshotRef,
    hasImmediateSnapshotChange,
    commitSnapshot: (value) => committed.push(value),
    getRefreshIntervalRef: { current: () => 5_000 },
    lastSpeedUpdateRef,
    pendingSnapshotRef,
    throttleTimerRef,
    setTimeout: (run) => {
      timerCount += 1;
      scheduled = run;
      return timerCount;
    },
    setSpeedSnapshot: (value) => committed.push(value),
    setIsLoading: () => undefined
  });

  const revision2 = snapshot({ revision: 2, totalBytesPerSecond: 200 });
  const revision3 = snapshot({ revision: 3, totalBytesPerSecond: 300 });
  renderAcceptedSnapshot(revision2, true);
  renderAcceptedSnapshot(revision3, true);
  assert.equal(timerCount, 1);
  assert.equal(pendingSnapshotRef.current.revision, 3);
  assert.deepEqual(committed, []);

  scheduled();
  assert.equal(renderedSnapshotRef.current.revision, 3);
  assert.equal(committed.at(-1).revision, 3);
});

test('the shipped request callback coalesces overlap and runs one immediate trailing fetch', async () => {
  const callback = liftHookCallback(
    'src/contexts/SpeedContext/index.tsx',
    'useCallback',
    'activeRequest.trailing = true'
  );
  const resolvers = [];
  const accepted = [];
  let fetches = 0;
  const ApiService = {
    getCurrentSpeeds: () => {
      fetches += 1;
      return new Promise((resolve) => resolvers.push(resolve));
    }
  };
  const inFlightRef = { current: null };
  const requestOwnerRef = { current: 7 };
  const requestSpeed = bindLifted(callback, {
    mockMode: false,
    applyMockSnapshot: () => undefined,
    requestOwnerRef,
    inFlightRef,
    ApiService,
    mountedRef: { current: true },
    acceptSnapshot: (value, owner, throttle) => accepted.push({ value, owner, throttle }),
    console,
    window: { dispatchEvent: () => true },
    CustomEvent: class {},
    APP_EVENTS: { SHOW_TOAST: 'show-toast' },
    i18n: { t: (key) => key },
    getErrorMessage: (error) => String(error),
    setIsLoading: () => undefined
  });

  const first = requestSpeed({ notifyFailure: false, throttle: true });
  const same = requestSpeed({ notifyFailure: true, throttle: false });
  assert.equal(first, same);
  assert.equal(fetches, 1);

  resolvers.shift()(snapshot());
  await Promise.resolve();
  assert.equal(fetches, 2);
  resolvers.shift()(snapshot({ revision: 2 }));
  await first;

  assert.deepEqual(
    accepted.map(({ throttle }) => throttle),
    [true, false]
  );
  assert.equal(inFlightRef.current, null);
});

test('the shipped request callback rejects a response from an earlier owner', async () => {
  const callback = liftHookCallback(
    'src/contexts/SpeedContext/index.tsx',
    'useCallback',
    'activeRequest.trailing = true'
  );
  let resolveRequest;
  let accepted = false;
  let loadingChanges = 0;
  const requestOwnerRef = { current: 3 };
  const requestSpeed = bindLifted(callback, {
    mockMode: false,
    applyMockSnapshot: () => undefined,
    requestOwnerRef,
    inFlightRef: { current: null },
    ApiService: {
      getCurrentSpeeds: () =>
        new Promise((resolve) => {
          resolveRequest = resolve;
        })
    },
    mountedRef: { current: true },
    acceptSnapshot: () => {
      accepted = true;
    },
    console,
    window: { dispatchEvent: () => true },
    CustomEvent: class {},
    APP_EVENTS: { SHOW_TOAST: 'show-toast' },
    i18n: { t: (key) => key },
    getErrorMessage: (error) => String(error),
    setIsLoading: () => {
      loadingChanges += 1;
    }
  });

  const pending = requestSpeed({ notifyFailure: false, throttle: false });
  requestOwnerRef.current = 4;
  resolveRequest(snapshot());
  await pending;
  assert.equal(accepted, false);
  assert.equal(loadingChanges, 0);
});
