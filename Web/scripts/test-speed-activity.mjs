import assert from 'node:assert/strict';
import test from 'node:test';
import {
  canAcceptRestSnapshot,
  canAcceptSignalRSnapshot,
  hasImmediateSnapshotChange,
  isDownloadSpeedSnapshot
} from '../src/contexts/SpeedContext/snapshot.ts';
import typescript from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

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

// ── The backend unreachable: the last snapshot ages out on screen ───────────

const SPEED_CONTEXT = 'src/contexts/SpeedContext/index.tsx';
const NOW = Date.parse('2026-09-30T12:00:10.000Z');
const expiredGame = game({
  key: 'steam|10.0.0.2|app:440',
  clientIp: '10.0.0.2',
  bytesPerSecond: 50,
  requestCount: 3,
  activeUntilUtc: '2026-09-30T12:00:08.000Z'
});
const lastSnapshot = snapshot({
  totalBytesPerSecond: 150,
  entriesInWindow: 5,
  gameSpeeds: [game(), expiredGame],
  clientSpeeds: [
    client(),
    client({ clientIp: '10.0.0.2', activeUntilUtc: '2026-09-30T12:00:08.000Z' })
  ]
});

/** The shipped request callback, failing every fetch, with the connection state given. */
const failingRequest = (
  isConnected,
  getCurrentSpeeds = () => Promise.reject(new Error('Server unreachable'))
) => {
  const committed = [];
  const outageCopyShownRef = { current: false };
  const isConnectedRef = { current: isConnected };
  const requestSpeed = bindLifted(
    liftHookCallback(SPEED_CONTEXT, 'useCallback', 'activeRequest.trailing = true'),
    {
      mockMode: false,
      applyMockSnapshot: () => undefined,
      requestOwnerRef: { current: 1 },
      inFlightRef: { current: null },
      ApiService: { getCurrentSpeeds },
      mountedRef: { current: true },
      acceptSnapshot: () => assert.fail('nothing arrives'),
      console: { error: () => undefined },
      window: { dispatchEvent: () => true },
      CustomEvent: class {},
      APP_EVENTS: { SHOW_TOAST: 'show-toast' },
      i18n: { t: (key) => key },
      getErrorMessage: (error) => String(error),
      setIsLoading: () => undefined,
      isConnectedRef,
      acceptedSnapshotRef: { current: lastSnapshot },
      outageCopyShownRef,
      commitSnapshot: (value) => committed.push(value),
      Date: { now: () => NOW, parse: Date.parse }
    }
  );
  return { requestSpeed, committed, outageCopyShownRef, isConnectedRef };
};

test('a failed poll during an outage shows the last snapshot without its expired rows', async () => {
  const { requestSpeed, committed, outageCopyShownRef } = failingRequest(false);
  await requestSpeed({ notifyFailure: false, throttle: true });
  assert.equal(committed.length, 1);
  const [copy] = committed;
  assert.deepEqual(
    copy.gameSpeeds.map((row) => row.key),
    [game().key]
  );
  assert.deepEqual(
    copy.clientSpeeds.map((row) => row.clientIp),
    ['10.0.0.1']
  );
  assert.equal(copy.totalBytesPerSecond, 100, 'the total drops with the rows that aged out');
  assert.equal(copy.entriesInWindow, 2);
  assert.equal(copy.hasActiveDownloads, true);
  assert.equal(copy.isAvailable, false);
  assert.equal(copy.revision, lastSnapshot.revision);
  assert.equal(outageCopyShownRef.current, true);

  // While the live connection still works, a failed poll leaves the screen alone.
  const connected = failingRequest(true);
  await connected.requestSpeed({ notifyFailure: false, throttle: true });
  assert.deepEqual(connected.committed, []);
  assert.equal(connected.outageCopyShownRef.current, false);
});

test('a slow failing request that outlives the live connection still shows the outage copy', async () => {
  const failures = [];
  const { requestSpeed, committed, isConnectedRef } = failingRequest(
    true,
    () => new Promise((_resolve, reject) => failures.push(reject))
  );
  const running = requestSpeed({ notifyFailure: false, throttle: true });
  // The connection drops while that request is still out, and the next poll joins it.
  isConnectedRef.current = false;
  requestSpeed({ notifyFailure: false, throttle: true });
  failures.shift()(new Error('Server unreachable'));
  await new Promise((resolve) => setImmediate(resolve));
  failures.shift()(new Error('Server unreachable'));
  await running;
  assert.equal(committed.length, 2, 'each failure after the drop renders the outage copy');
  assert.equal(committed[0].isAvailable, false);
});

test('the outage copy replaces a throttled snapshot still waiting to render', () => {
  const pendingSnapshotRef = { current: snapshot({ revision: 2 }) };
  const throttleTimerRef = { current: 7 };
  const cleared = [];
  const rendered = [];
  const commitSnapshot = bindLifted(
    liftHookCallback(SPEED_CONTEXT, 'useCallback', 'clearThrottle();'),
    {
      clearThrottle: bindLifted(
        liftHookCallback(SPEED_CONTEXT, 'useCallback', 'clearTimeout(throttleTimerRef.current)'),
        {
          throttleTimerRef,
          pendingSnapshotRef,
          clearTimeout: (id) => cleared.push(id)
        }
      ),
      renderedSnapshotRef: { current: null },
      lastSpeedUpdateRef: { current: 0 },
      setSpeedSnapshot: (value) => rendered.push(value),
      setIsLoading: () => undefined
    }
  );
  const copy = { ...lastSnapshot, isAvailable: false };
  commitSnapshot(copy);
  assert.deepEqual(cleared, [7]);
  assert.equal(pendingSnapshotRef.current, null, 'the waiting snapshot can no longer land');
  assert.deepEqual(rendered, [copy]);
});

test('the next snapshot after an outage renders even at the revision the copy was made from', () => {
  for (const outage of [true, false]) {
    const outageCopyShownRef = { current: outage };
    const rendered = [];
    const acceptSnapshot = bindLifted(
      liftHookCallback(SPEED_CONTEXT, 'useCallback', 'canAcceptRestSnapshot(current, value)'),
      {
        requestOwnerRef: { current: 1 },
        mountedRef: { current: true },
        mockMode: false,
        isDownloadSpeedSnapshot,
        acceptedSnapshotRef: { current: lastSnapshot },
        canAcceptRestSnapshot,
        outageCopyShownRef,
        renderAcceptedSnapshot: (value) => rendered.push(value)
      }
    );
    const same = snapshot({ revision: lastSnapshot.revision });
    assert.equal(acceptSnapshot(same, 1, true), outage, `outage ${outage}`);
    assert.deepEqual(rendered, outage ? [same] : []);
    assert.equal(outageCopyShownRef.current, false);
  }

  // A push at the same revision ends the outage copy the same way.
  let handler;
  const outageCopyShownRef = { current: true };
  const rendered = [];
  bindLifted(liftHookCallback(SPEED_CONTEXT, 'useEffect', 'canAcceptSignalRSnapshot'), {
    mockMode: false,
    isDownloadSpeedSnapshot,
    acceptedSnapshotRef: { current: lastSnapshot },
    fetchSpeed: () => assert.fail('the push belongs to the shown stream'),
    canAcceptSignalRSnapshot,
    outageCopyShownRef,
    renderAcceptedSnapshot: (value) => rendered.push(value),
    signalR: {
      on: (_name, callback) => {
        handler = callback;
      },
      off: () => undefined
    }
  })();
  const pushed = snapshot({ revision: lastSnapshot.revision });
  handler(pushed);
  assert.deepEqual(rendered, [pushed]);
  assert.equal(outageCopyShownRef.current, false);
});

test('during an outage the activity view says updates stopped, with or without rows', () => {
  const viewPath = 'src/components/features/downloads/ActiveDownloadsView.tsx';
  const viewSource = parseSource(viewPath, typescript.ScriptKind.TSX);
  const view = findSoleNode(
    viewSource,
    'ActiveDownloadsView',
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(viewSource) === 'ActiveDownloadsView'
  ).initializer.getText(viewSource);
  const h = {
    createElement: (type, attributes, ...children) => ({
      type,
      props: { ...attributes, children: children.flat(Infinity) }
    }),
    Fragment: 'Fragment'
  };
  const render = (speedSnapshot, connectionLost) =>
    bindLifted(
      view,
      {
        React: h,
        useTranslation: () => ({ t: (key) => key }),
        useSpeed: () => ({
          speedSnapshot,
          gameSpeeds: speedSnapshot.gameSpeeds,
          clientSpeeds: speedSnapshot.clientSpeeds,
          isLoading: false,
          refreshSpeed: () => undefined
        }),
        useActivityStatus: () => ({ isActive: () => false }),
        useConnectionLost: () => connectionLost,
        useState: (initial) => [initial, () => undefined],
        EmptyState: 'EmptyState',
        LoadingState: 'LoadingState',
        Alert: 'Alert',
        Activity: 'Activity'
      },
      { jsx: typescript.JsxEmit.React }
    )();
  const nodes = (tree) =>
    !tree || typeof tree !== 'object' ? [] : [tree, ...tree.props.children.flatMap(nodes)];
  const agedOut = snapshot({
    isAvailable: false,
    totalBytesPerSecond: 0,
    entriesInWindow: 0,
    gameSpeeds: [],
    clientSpeeds: [],
    hasActiveDownloads: false
  });

  const outage = nodes(render(agedOut, true));
  assert.deepEqual(
    outage.filter((node) => node.type === 'Alert').map((node) => node.props.title),
    ['downloads.activity.unavailableTitle']
  );
  assert.equal(
    outage.some((node) => node.type === 'EmptyState'),
    false
  );

  // A server with nothing to track sends the same shape while connected: no promise of updates.
  const untracked = nodes(render(agedOut, false));
  assert.equal(
    untracked.some((node) => node.type === 'Alert'),
    false
  );
  assert.deepEqual(
    untracked.filter((node) => node.type === 'EmptyState').map((node) => node.props.title),
    ['downloads.activity.waitingTitle']
  );
});
