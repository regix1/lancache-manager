import assert from 'node:assert/strict';
import test from 'node:test';
import {
  bindLifted,
  liftConstArrow,
  liftHookCallback,
  MemoryStorage
} from './transpile-module.mjs';

const path = 'src/App.tsx';
const cancelSource = liftHookCallback(path, 'useCallback', 'fullScanRequestRef.current?.abort()');
const refreshSource = liftHookCallback(path, 'useCallback', 'ApiService.getSchedules');
const eventsSource = liftHookCallback(path, 'useEffect', "signalR.on('AutomaticScanSkipped'");
const sessionSource = liftHookCallback(
  path,
  'useEffect',
  'setShowFullScanRequiredModal(false);\n    fullScanActionRunningRef.current = false;'
);
const dismissSource = liftConstArrow(path, 'handleFullScanModalDismiss');

const settled = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};
const required = (isRunning = false) => [
  {
    key: 'depotMapping',
    isRunning,
    pendingFullScan: { changeGap: 1234, estimatedAppsToScan: 2468 }
  }
];
const current = [{ key: 'depotMapping', isRunning: false, pendingFullScan: null }];

const mount = (authMode = 'authenticated') => {
  const state = { visible: false, gap: undefined, apps: undefined };
  const handlers = new Map();
  const windowHandlers = new Map();
  const requests = [];
  const errors = [];
  const sessionStore = new MemoryStorage();
  const fullScanActionRunningRef = { current: false };
  const fullScanRequestRef = { current: null };
  const bindings = {
    authMode,
    fullScanActionRunningRef,
    fullScanRequestRef,
    sessionStore,
    wasModalDismissed: () => sessionStore.getItem('fullScanModalDismissed') === 'true',
    markModalDismissed: () => sessionStore.setItem('fullScanModalDismissed', 'true'),
    setShowFullScanRequiredModal: (value) => {
      state.visible = value;
    },
    setFullScanModalChangeGap: (value) => {
      state.gap = value;
    },
    setFullScanModalEstimatedApps: (value) => {
      state.apps = value;
    },
    ApiService: {
      getSchedules: (signal) =>
        new Promise((resolve, reject) => requests.push({ signal, resolve, reject }))
    },
    notifyError: (...args) => errors.push(args),
    t: (key) => key,
    signalR: {
      on: (name, handler) => handlers.set(name, handler),
      off: (name, handler) => {
        if (handlers.get(name) === handler) handlers.delete(name);
      }
    },
    window: {
      addEventListener: (name, handler) => windowHandlers.set(name, handler),
      removeEventListener: (name, handler) => {
        if (windowHandlers.get(name) === handler) windowHandlers.delete(name);
      }
    },
    APP_EVENTS: { SHOW_FULL_SCAN_MODAL: 'show-full-scan-modal' }
  };
  bindings.cancelFullScanCheck = bindLifted(cancelSource, bindings);
  bindings.refreshFullScanPrompt = bindLifted(refreshSource, bindings);
  let cleanup;
  const render = () => {
    cleanup?.();
    cleanup = bindLifted(eventsSource, { ...bindings, showFullScanRequiredModal: state.visible })();
  };
  render();
  return {
    state,
    requests,
    errors,
    fullScanActionRunningRef,
    fullScanRequestRef,
    sessionStore,
    render,
    emit: (name, event) => handlers.get(name)?.(event),
    reopen: () => windowHandlers.get('show-full-scan-modal')?.(),
    dismiss: bindLifted(dismissSource, bindings),
    resetSession: bindLifted(sessionSource, bindings),
    unmount: () => cleanup?.()
  };
};

test('a delayed skipped event cannot open the prompt after mappings were imported', async () => {
  const app = mount();
  app.emit('DepotMappingComplete', { success: true, status: 'completed' });
  app.emit('AutomaticScanSkipped', { context: { changeGap: 999999 } });
  assert.equal(app.state.visible, false);
  app.requests[0].resolve(current);
  await settled();
  assert.equal(app.state.visible, false);
});

test('a current requirement opens with the latest server figures', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped', { context: { changeGap: 999999 } });
  app.requests[0].resolve(required());
  await settled();
  assert.deepEqual(app.state, { visible: true, gap: 1234, apps: 2468 });
});

test('an import completion rejects an older response even when abort is ignored', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped');
  app.emit('DepotMappingComplete', { success: true, status: 'completed' });
  assert.equal(app.requests[0].signal.aborted, true);
  app.requests[0].resolve(required());
  await settled();
  assert.equal(app.state.visible, false);
});

test('a completed import dismisses an already open prompt', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped');
  app.requests[0].resolve(required());
  await settled();
  app.render();
  app.emit('DepotMappingComplete', { success: true, status: 'completed' });
  assert.equal(app.state.visible, false);
});

test('only the latest request can publish a requirement', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped');
  app.emit('AutomaticScanSkipped');
  app.requests[1].resolve(current);
  await settled();
  app.requests[0].resolve(required());
  await settled();
  assert.equal(app.state.visible, false);
});

test('a genuine skipped scan opens after the schedule has stopped running', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped');
  app.requests[0].resolve(required(true));
  await settled();
  assert.equal(app.state.visible, false);
  app.emit('DepotMappingComplete', { success: false, status: 'skipped' });
  app.emit('SchedulesUpdated', required());
  app.requests[1].resolve(required());
  await settled();
  assert.equal(app.state.visible, true);
});

test('a newer schedule hint revalidates an open prompt', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped');
  app.requests[0].resolve(required());
  await settled();
  app.render();
  app.emit('SchedulesUpdated', current);
  app.requests[1].resolve(current);
  await settled();
  assert.equal(app.state.visible, false);
});

test('dismissal cancels an in-flight check and automatic events respect it', async () => {
  const app = mount();
  app.emit('AutomaticScanSkipped');
  app.dismiss();
  app.requests[0].resolve(required());
  await settled();
  app.emit('AutomaticScanSkipped');
  app.emit('SchedulesUpdated', required());
  assert.equal(app.requests.length, 1);
  assert.equal(app.state.visible, false);
  app.reopen();
  app.requests[1].resolve(current);
  await settled();
  assert.equal(app.state.visible, false);
});

test('mapping start, account change and unmount invalidate pending checks', async () => {
  for (const invalidate of [
    (app) => app.emit('DepotMappingStarted'),
    (app) => app.resetSession(),
    (app) => app.unmount()
  ]) {
    const app = mount();
    app.emit('AutomaticScanSkipped');
    invalidate(app);
    app.requests[0].resolve(required());
    await settled();
    assert.equal(app.state.visible, false);
    assert.equal(app.requests[0].signal.aborted, true);
  }
});

test('failed verification never fabricates a full-scan requirement', async () => {
  for (const manual of [false, true]) {
    const app = mount();
    if (manual) app.reopen();
    else app.emit('AutomaticScanSkipped');
    app.requests[0].reject(new Error('Server unavailable'));
    await settled();
    assert.equal(app.state.visible, false);
    assert.equal(app.errors.length, 1);
    assert.equal(app.errors[0][2].silent, !manual);
  }
});

test('an action failure can expose a still-current requirement and success releases the action', async () => {
  const app = mount();
  app.fullScanActionRunningRef.current = true;
  app.emit('AutomaticScanSkipped');
  assert.equal(app.requests.length, 0);
  app.emit('DepotMappingComplete', { success: false, status: 'failed' });
  app.requests[0].resolve(required());
  await settled();
  assert.equal(app.state.visible, true);
  app.emit('DepotMappingComplete', { success: true, status: 'completed' });
  assert.equal(app.fullScanActionRunningRef.current, false);
  assert.equal(app.state.visible, false);
});

test('unauthenticated sessions do not subscribe to the prompt events', () => {
  const app = mount('guest');
  app.emit('AutomaticScanSkipped');
  app.reopen();
  assert.equal(app.requests.length, 0);
});
