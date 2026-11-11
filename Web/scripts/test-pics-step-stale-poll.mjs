import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow, liftHookCallback } from './transpile-module.mjs';

/**
 * A Steam depot scan sends its completion, and only then lets go of its Steam connection; the
 * server keeps answering the progress poll with `isProcessing: true` until that disconnect is done,
 * while the poll's phase already reads `completed`. A poll answered in that window (the page's
 * first read, or the read after a SignalR reconnect) can be the last word the setup wizard gets:
 * the completion event was lost while the socket was down, or arrived before the page had any
 * progress to fold it into. The depot step has to move on all the same, not wait until a reload.
 *
 * The poll, the completion handler and the step's "finished" effect are lifted from the shipped
 * files. After every state change the effect runs once, as React runs it after each render.
 */

const stepPath = 'src/components/initialization/steps/PicsProgressStep.tsx';
const contextPath = 'src/contexts/PicsProgressContext.tsx';
const finishedEffectSource = liftHookCallback(stepPath, 'useEffect', 'setIsComplete(true)');
const fetchProgressSource = liftConstArrow(contextPath, 'fetchProgress');
const handleCompleteSource = liftConstArrow(contextPath, 'handleDepotMappingComplete');

/** The state a scan near its end leaves in the page (and in its session cache). */
const runningScan = {
  isProcessing: true,
  status: 'running',
  totalApps: 5000,
  processedApps: 5000,
  totalBatches: 10,
  processedBatches: 10,
  progressPercent: 100,
  depotMappingsFound: 1200,
  depotMappingsFoundInSession: 40,
  crawlIntervalHours: 1,
  crawlIncrementalMode: true,
  isReady: true,
  isConnected: true,
  isLoggedOn: true,
  isWebApiAvailable: true,
  errorMessage: null
};

/** GET /api/depots/rebuild/progress answered after the completion, before the disconnect ended. */
const pollInTheWindow = { ...runningScan, isProcessing: true, status: 'completed' };

const completionEvent = {
  serviceKey: 'depotMapping',
  operationId: '6d1f0c3e-2b7a-4f7e-9a51-0b8c2d4e6f10',
  success: true,
  canceled: false,
  status: 'completed',
  stageKey: 'signalr.depotMapping.finalized',
  context: {},
  percentComplete: 100,
  totalApps: 5000,
  totalBatches: 10,
  depotMappingsFound: 1200,
  error: null
};

/**
 * One page with the wizard's depot step open. `start` is what the page held before the poll and
 * the event: the cached running scan, or nothing when the page has just loaded.
 */
const openStep = (start) => {
  const page = { progress: start, isComplete: false, advanced: false };
  const setProgress = (update) => {
    page.progress = typeof update === 'function' ? update(page.progress) : update;
    runFinishedEffect();
  };
  const runFinishedEffect = () =>
    bindLifted(finishedEffectSource, {
      progress: page.progress,
      isComplete: page.isComplete,
      setIsComplete: (value) => {
        page.isComplete = value;
      },
      completeTimerRef: { current: null },
      setTimeout: (callback) => {
        callback();
        return 0;
      },
      onComplete: () => {
        page.advanced = true;
      }
    })();

  let answerPoll;
  const reply = new Promise((resolve) => {
    answerPoll = resolve;
  });
  const fetchProgress = bindLifted(fetchProgressSource, {
    mockMode: false,
    authLoading: false,
    hasAccess: true,
    fetch: () => reply,
    ApiService: { getFetchOptions: () => ({}) },
    setProgress,
    sessionStore: { setItem: () => undefined },
    setIsLoading: () => undefined,
    console
  });
  const handleDepotMappingComplete = bindLifted(handleCompleteSource, { setProgress });

  runFinishedEffect();
  return {
    page,
    sendPoll: () => fetchProgress(true),
    answerPoll: () => answerPoll({ ok: true, json: async () => ({ ...pollInTheWindow }) }),
    deliverEvent: () => handleDepotMappingComplete({ ...completionEvent })
  };
};

const flush = async () => {
  for (let turn = 0; turn < 5; turn += 1) await new Promise((done) => setImmediate(done));
};

const starts = [
  { label: 'the page held the running scan', state: runningScan },
  { label: 'the page had just loaded', state: null }
];

/** Every order of the poll and the completion event, including the event never arriving. */
const deliveries = [
  { label: 'poll sent, event, poll answered', steps: ['send', 'event', 'answer'] },
  { label: 'poll sent, poll answered, event', steps: ['send', 'answer', 'event'] },
  { label: 'event, poll sent, poll answered', steps: ['event', 'send', 'answer'] },
  { label: 'event lost, poll sent, poll answered', steps: ['send', 'answer'] }
];

test('the depot step finishes when the last poll answers completed while the scan still disconnects', async () => {
  const stuck = [];
  for (const start of starts) {
    for (const delivery of deliveries) {
      const step = openStep(start.state);
      let polling = Promise.resolve();
      for (const name of delivery.steps) {
        if (name === 'send') polling = step.sendPoll();
        if (name === 'answer') step.answerPoll();
        if (name === 'event') step.deliverEvent();
        await flush();
      }
      await polling;
      if (!step.page.advanced) stuck.push(`${start.label}; ${delivery.label}`);
    }
  }
  assert.deepEqual(
    stuck,
    [],
    `the depot step stays on "processing" after the scan completed, until a reload, when: ${stuck.join(' | ')}`
  );
});
