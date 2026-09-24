import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  findSoleNode,
  loadNotificationModules,
  moduleUrl,
  operationRunRow,
  parseSource,
  prefillRunFields,
  pushRun
} from './transpile-module.mjs';

/**
 * A scheduled prefill card opens on its run row and takes its text from the registry's progress
 * getters, handed to the run store the way the provider hands them. These cases drive the real
 * registry entry, detail builders and store, and read the card the bar would draw.
 */

const I18N = moduleUrl(`
export default {
  t: (key, values) =>
    values
      ? key + ':' + Object.entries(values).map(([name, value]) => name + '=' + value).join(',')
      : key,
  exists: (key) => key.startsWith('signalr.')
};`);

const modules = await loadNotificationModules(I18N);
const entry = modules.NOTIFICATION_REGISTRY.find((item) => item.type === 'scheduled_prefill');

const liveEvent = (percentComplete, game, bytesDownloaded) => ({
  operationId: 'operation-steam',
  serviceId: 'Steam',
  stage: 'running',
  stageKey:
    percentComplete === null
      ? 'signalr.scheduledPrefill.downloadingGameUnknownTotal'
      : 'signalr.scheduledPrefill.downloadingGame',
  stageContext: percentComplete === null ? { game } : { game, completed: 1, total: 3 },
  message: `Downloading ${game}`,
  percentComplete,
  bytesDownloaded,
  totalBytes: 2048
});

test('scheduled progress clears stale percent when the daemon total becomes unknown', () => {
  let state = pushRun(
    modules,
    modules.createRunStoreState(),
    operationRunRow('operation-steam', prefillRunFields)
  );
  // The detail builders hand their patch to the store the way the provider's dispatch does.
  const onProgress = modules.buildProgressHandler(entry, entry.progress, (id, build, source) => {
    state = modules.applyDetail(state, id, build, source, { requestSeq: 0 });
  });

  onProgress(liveEvent(1, 'First game', 128));
  assert.equal(modules.deriveNotifications(state, [])[0].progress, 1);
  onProgress(liveEvent(null, 'Second game', 512));

  const cards = modules.deriveNotifications(state, []);
  assert.equal(cards.length, 1);
  assert.equal(cards[0].progress, undefined);
  assert.match(cards[0].message, /Second game/);
  assert.match(cards[0].detailMessage, /downloaded=512\.00 B/);

  const recovered = entry.recovery.recoverCards({
    services: [{ ...liveEvent(null, 'Second game', 512), isRunning: true }]
  });
  assert.equal(recovered[0].progress, undefined);
  assert.match(recovered[0].message, /Second game/);
});

test('scheduled reconnect preserves both cards and rejects replayed and ended progress', () => {
  let state = pushRun(
    modules,
    modules.createRunStoreState(),
    operationRunRow('operation-steam', prefillRunFields)
  );
  state = pushRun(
    modules,
    state,
    operationRunRow('operation-two', { ...prefillRunFields, serviceId: 'Epic' })
  );
  const cards = () => modules.deriveNotifications(state, []);
  const progress = modules.buildProgressHandler(entry, entry.progress, (id, build, source) => {
    state = modules.applyDetail(state, id, build, source, { requestSeq: 0 });
  });
  const first = {
    ...liveEvent(42, 'First game', 420),
    eventEpoch: 'series-a',
    eventSequence: 10,
    daemonInstanceId: 'daemon'
  };
  const second = {
    ...liveEvent(31, 'Second game', 310),
    operationId: 'operation-two',
    serviceId: 'Epic',
    eventEpoch: 'series-b',
    eventSequence: 20,
    daemonInstanceId: 'daemon'
  };
  progress(first);
  progress(second);
  const text = (card) => [card.id, card.message, card.progress, card.detailMessage];
  const initial = cards().map(text);
  assert.equal(initial.length, 2);

  // The connection dropped: both cards stay and say they are reconnecting.
  state = modules.markConnectionRecovering(state);
  assert.deepEqual(cards().map(text), initial);
  assert.equal(
    cards().every((card) => card.details.connectionRecovering),
    true
  );

  // A recovering stage carries no new line, so the card keeps its message, percent and detail.
  progress({
    ...first,
    eventSequence: 11,
    stage: 'recovering',
    stageKey: null,
    message: 'Reconnecting',
    percentComplete: null,
    bytesDownloaded: null
  });
  assert.deepEqual(cards().map(text), initial);
  assert.equal(cards()[0].details.recovering, true);

  // Replays and another daemon's events change nothing.
  const before = state;
  for (const event of [
    first,
    { ...first, eventSequence: 11 },
    { ...first, eventSequence: 100, daemonInstanceId: 'old-daemon' }
  ])
    progress(event);
  assert.equal(state, before);

  progress({ ...first, eventSequence: 12, bytesDownloaded: 450 });
  assert.equal(cards()[0].details.recovering, false);
  assert.match(cards()[0].detailMessage, /downloaded=450\.00 B/);
  assert.deepEqual(cards()[1].message, initial[1][1]);

  // The run ended: a late progress tick cannot reopen or rewrite the finished card.
  state = pushRun(
    modules,
    state,
    operationRunRow('operation-steam', {
      ...prefillRunFields,
      status: 'completed',
      retained: true
    })
  );
  const ended = cards().find((card) => card.id === 'operation-steam');
  assert.equal(ended.status, 'completed');
  progress({ ...first, eventSequence: 99, percentComplete: 5 });
  assert.deepEqual(
    cards().find((card) => card.id === 'operation-steam'),
    ended
  );
});

test('scheduled announcements throttle ordinary text and announce lifecycle transitions once', () => {
  const source = parseSource(
    'src/components/common/UnifiedNotificationItem.tsx',
    ts.ScriptKind.TSX
  );
  const declaration = findSoleNode(
    source,
    'announcement hook',
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'useNotificationAnnouncement'
  );
  let now = 10000;
  let state;
  let ref;
  const announced = [];
  const hook = bindLifted(declaration.getText(source), {
    useTranslation: () => ({ t: (key) => key }),
    useState: (initial) => {
      state ??= initial;
      return [
        state,
        (value) => {
          state = value;
          announced.push(value);
        }
      ];
    },
    useRef: (initial) => (ref ??= { current: initial }),
    useEffect: (effect) => effect(),
    Date: { now: () => now },
    ANNOUNCEMENT_MIN_INTERVAL_MS: 5000,
    isTerminalNotificationStatus: (status) =>
      ['completed', 'failed', 'cancelled', 'skipped'].includes(status)
  });
  const card = {
    type: 'scheduled_prefill',
    status: 'running',
    message: 'Game one',
    progress: 10,
    details: {}
  };
  hook(card);
  for (let index = 0; index < 25; index++) {
    now += 100;
    hook({ ...card, message: `Game ${index}`, progress: 10 + index / 10 });
  }
  assert.equal(announced.length, 0);
  now = 15000;
  hook({ ...card, message: 'Latest game', progress: 30 });
  assert.equal(announced.length, 1);
  const recovering = { ...card, message: 'Latest game', details: { recovering: true } };
  hook(recovering);
  assert.equal(announced.length, 2);
  for (let index = 0; index < 25; index++) hook(recovering);
  assert.equal(announced.length, 2);
  hook({ ...recovering, status: 'cancelling' });
  assert.equal(announced.length, 3);
  assert.match(announced[2], /prefill.progress.cancelling/);
  const terminal = { ...card, message: 'Finished', status: 'completed' };
  hook(terminal);
  hook(terminal);
  assert.equal(announced.length, 4);

  for (const status of ['completed', 'failed', 'cancelled', 'skipped']) {
    for (const progressAriaValueText of [undefined, 'Everything was already cached']) {
      state = undefined;
      ref = undefined;
      announced.length = 0;
      const running = {
        ...card,
        message: 'Everything was already cached',
        progressAriaValueText
      };
      hook(running);
      now += 5000;
      hook({ ...running });
      assert.equal(announced.length, 0, 'ordinary same-text progress remains silent');
      hook({ ...running, status });
      assert.equal(announced.length, 1, 'same-text terminal announces once');
      assert.equal(announced[0], `prefill.runs.${status} Everything was already cached`);
      for (let index = 0; index < 25; index++) {
        now += 5000;
        hook({ ...running, status });
      }
      assert.equal(announced.length, 1, 'duplicate terminal remains silent');
    }
  }
});
