import assert from 'node:assert/strict';
import test from 'node:test';
import {
  loadNotificationModules,
  moduleUrl,
  operationRunRow,
  prefillRunFields,
  pushRun
} from './transpile-module.mjs';

/**
 * The schedule's watch loop sends a bare "Prefill in progress" line, keyed
 * `signalr.scheduledPrefill.running`, on its first pass and when a run resumes after a reconnect.
 * The resume follows a replay of the run's counts line, so the bare line must not replace it.
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

test('a resume after a reconnect keeps the replayed counts line and its percent', () => {
  let state = pushRun(
    modules,
    modules.createRunStoreState(),
    operationRunRow('operation-steam', prefillRunFields)
  );
  const cards = () => modules.deriveNotifications(state, []);
  const progress = modules.buildProgressHandler(entry, entry.progress, (id, build, source) => {
    state = modules.applyDetail(state, id, build, source, { requestSeq: 0 });
  });
  const base = {
    operationId: 'operation-steam',
    serviceId: 'Steam',
    eventEpoch: 'series-a',
    daemonInstanceId: 'daemon'
  };

  progress({
    ...base,
    eventSequence: 10,
    stage: 'recovering',
    stageKey: 'signalr.scheduledPrefill.reconnecting',
    message: 'Waiting for the prefill daemon to reconnect',
    percentComplete: null
  });
  // The resume replays the run's latest counts line first.
  progress({
    ...base,
    eventSequence: 11,
    stage: 'running',
    stageKey: 'signalr.scheduledPrefill.runningWithCounts',
    stageContext: { completed: 3, total: 10 },
    message: 'Prefill in progress (3 of 10 games)',
    percentComplete: 30
  });
  const replayed = cards()[0];
  assert.match(replayed.message, /runningWithCounts/);
  assert.equal(replayed.progress, 30);

  progress({
    ...base,
    eventSequence: 12,
    stage: 'running',
    stageKey: 'signalr.scheduledPrefill.running',
    message: 'Prefill in progress',
    percentComplete: null
  });
  assert.equal(cards()[0].message, replayed.message);
  assert.equal(cards()[0].progress, 30);
});
