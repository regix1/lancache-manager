import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  moduleUrl,
  parseSource,
  transpile
} from './transpile-module.mjs';

const reactUrl = moduleUrl(`
export const useState = (initial) => {
  const state = globalThis.scheduleModesTest;
  const slot = { value: initial };
  state.slots.push(slot);
  return [slot.value, (value) => { slot.value = value; }];
};
export const useRef = (initial) => ({ current: initial });
export const useCallback = (callback) => callback;
export const useEffect = (effect) => { effect(); };
`);

const apiUrl = moduleUrl(`
export default {
  getSchedules: () => {
    const state = globalThis.scheduleModesTest;
    const request = state.defer();
    state.schedules.push(request);
    return request.promise;
  },
  getGlobalNotificationDisplayMode: () => {
    const state = globalThis.scheduleModesTest;
    const request = state.defer();
    state.defaults.push(request);
    return request.promise;
  }
};
`);
const signalRUrl = moduleUrl(`
export const useSignalR = () => {
  const state = globalThis.scheduleModesTest;
  return {
    on: (name, handler) => state.handlers.set(name, handler),
    off: (name) => state.handlers.delete(name),
    invoke: () => {
      const request = state.defer();
      state.joins.push(request);
      return request.promise;
    },
    isConnected: true
  };
};
`);
const reconnectUrl = moduleUrl(`
export const useReconnectRefetch = (_connected, callback) => {
  globalThis.scheduleModesTest.reconnect = callback;
};
`);
const hookUrl = await compileToUrl('../src/hooks/useScheduleDisplayModes.ts', {
  react: reactUrl,
  '@services/api.service': apiUrl,
  '@contexts/SignalRContext/useSignalR': signalRUrl,
  '@hooks/useReconnectRefetch': reconnectUrl
});
const { useScheduleDisplayModes } = await import(hookUrl);
const { getNotificationStyleOptions } = await import(
  await compileToUrl('../src/components/features/management/schedules/constants.ts')
);

const defer = () => {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
};
const settle = () => new Promise((resolve) => setImmediate(resolve));

const start = () => {
  const state = {
    slots: [],
    handlers: new Map(),
    joins: [],
    schedules: [],
    defaults: [],
    listeners: new Map(),
    defer,
    reconnect: null
  };
  globalThis.scheduleModesTest = state;
  globalThis.document = {
    visibilityState: 'visible',
    addEventListener: (name, handler) => state.listeners.set(name, handler),
    removeEventListener: (name) => state.listeners.delete(name)
  };
  state.initial = useScheduleDisplayModes();
  state.push = (name, value) => state.handlers.get(name)(value);
  state.finish = async (join, schedules, mode) => {
    state.joins[join].resolve();
    await settle();
    state.schedules[join].resolve(schedules);
    state.defaults[join].resolve(mode);
    await settle();
  };
  return state;
};

const row = (key, mode) => ({ key, notificationDisplayMode: mode });

test('mount waits for the group join, then reads both values and ignores older GET answers', async () => {
  const state = start();
  assert.equal(state.initial.defaultMode, 'full');
  assert.equal(state.joins.length, 1);
  assert.equal(state.schedules.length, 0);
  state.joins[0].resolve();
  await settle();
  assert.equal(state.schedules.length, 1);
  assert.equal(state.defaults.length, 1);
  state.push('SchedulesUpdated', [row('cacheReconciliation', 'full')]);
  state.push('NotificationDisplayModeChanged', { mode: 'full' });
  state.schedules[0].resolve([row('cacheReconciliation', 'condensed')]);
  state.defaults[0].resolve('condensed');
  await settle();
  assert.equal(state.slots[0].value.cacheReconciliation, 'full');
  assert.equal(state.slots[1].value, 'full');
  state.push('NotificationDisplayModeChanged', { mode: 'condensed' });
  assert.equal(state.slots[1].value, 'condensed');

  state.reconnect();
  assert.equal(state.joins.length, 2);
  assert.equal(state.schedules.length, 1);
  state.joins[1].reject(new Error('connection dropped'));
  await settle();
  assert.equal(state.schedules.length, 2);
  assert.equal(state.defaults.length, 2);
  state.schedules[1].resolve([row('cacheReconciliation', 'condensed')]);
  state.defaults[1].resolve('condensed');
  await settle();
  assert.equal(state.slots[1].value, 'condensed');
  assert.equal(state.slots[0].value.cacheReconciliation, 'condensed');
});

test('the styles count as known only once the first read settles, answered or not', async () => {
  for (const answered of [true, false]) {
    const state = start();
    assert.equal(state.initial.ready, false, 'the first render waits');
    state.joins[0].resolve();
    await settle();
    state.schedules[0].resolve([row('cacheReconciliation', 'full')]);
    await settle();
    assert.equal(state.slots[2].value, false, 'one read is still out');
    if (answered) state.defaults[0].resolve('condensed');
    else state.defaults[0].reject(new Error('read failed'));
    await settle();
    assert.equal(state.slots[2].value, true, answered ? 'answered' : 'failed');
  }
});

test('failed setting reads retry on a run push or visible tab and stop retrying after success', async () => {
  const state = start();
  await state.finish(0, [row('cacheReconciliation', 'full')], 'full');
  state.reconnect();
  state.joins[1].resolve();
  await settle();
  state.schedules[1].resolve([row('cacheReconciliation', 'full')]);
  state.defaults[1].reject(new Error('read failed'));
  await settle();
  state.push('OperationUpdated', {});
  assert.equal(state.joins.length, 3);
  assert.equal(state.defaults.length, 2);
  await state.finish(2, [row('cacheReconciliation', 'condensed')], 'condensed');
  assert.equal(state.slots[1].value, 'condensed');
  state.push('OperationUpdated', {});
  assert.equal(state.joins.length, 3);

  state.reconnect();
  state.joins[3].resolve();
  await settle();
  state.schedules[3].reject(new Error('read failed'));
  state.defaults[3].resolve('condensed');
  await settle();
  state.listeners.get('visibilitychange')();
  assert.equal(state.joins.length, 5);
  assert.equal(state.schedules.length, 4);
  await state.finish(4, [row('cacheReconciliation', 'full')], 'full');
  assert.equal(state.slots[0].value.cacheReconciliation, 'full');
  assert.equal(state.slots[1].value, 'full');
});

test('the page offers Default only on rows and clears an override through DELETE', async () => {
  const en = JSON.parse(readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url)));
  const zh = JSON.parse(readFileSync(new URL('../src/i18n/locales/zh.json', import.meta.url)));
  const t = (key, values = {}) => {
    const text = key.split('.').reduce((part, name) => part[name], en);
    return text.replace('{{style}}', values.style);
  };
  assert.deepEqual(
    getNotificationStyleOptions(t).map((option) => option.value),
    ['full', 'condensed']
  );
  const options = getNotificationStyleOptions(t, 'condensed');
  assert.equal(options[0].value, 'default');
  assert.equal(options[0].label, 'Default (Compact bar)');
  assert.equal(zh.management.schedules.notificationStyleDefault, '默认（{{style}}）');
  for (const locale of [en, zh]) {
    assert.ok(locale.common.notifications.failedTimesInRow);
    assert.ok(locale.common.notifications.failedTimesInRow_other);
    assert.ok(locale.common.notifications.latestRunSucceeded);
  }

  const source = liftHookCallback(
    'src/components/features/management/schedules/SchedulesSection.tsx',
    'useCallback',
    'ApiService.clearScheduleNotificationDisplayMode'
  );
  const calls = [];
  let schedules = [
    {
      key: 'cacheReconciliation',
      notificationDisplayMode: 'full',
      notificationDisplayModeOverridden: true
    }
  ];
  const change = bindLifted(source, {
    t: (key) => key,
    defaultMode: 'condensed',
    setSchedules: (update) => {
      schedules = update(schedules);
    },
    ApiService: {
      clearScheduleNotificationDisplayMode: async (key) => calls.push(['delete', key]),
      setScheduleNotificationDisplayMode: async (key, mode) => calls.push(['put', key, mode])
    },
    fetchSchedules: async () => calls.push(['refresh']),
    notifyError: (message, error) => calls.push(['error', message, error])
  });
  await change('cacheReconciliation', 'default');
  assert.deepEqual(calls, [['delete', 'cacheReconciliation'], ['refresh']]);
  assert.equal(schedules[0].notificationDisplayModeOverridden, false);
  assert.equal(schedules[0].notificationDisplayMode, 'condensed');
  await change('cacheReconciliation', 'full');
  assert.equal(schedules[0].notificationDisplayModeOverridden, true);
  assert.deepEqual(calls.at(-2), ['put', 'cacheReconciliation', 'full']);

  const page = parseSource(
    'src/components/features/management/schedules/SchedulesSection.tsx',
    ts.ScriptKind.TSX
  );
  const text = page.getFullText();
  assert.match(
    text,
    /service\.notificationDisplayModeOverridden\s*\? service\.notificationDisplayMode\s*: 'default'/
  );
  assert.match(text, /options=\{getNotificationStyleOptions\(t\)\}/);
  assert.match(text, /value=\{defaultMode\}/);
});

test('closing a kept run accepts 204 and 404 and rejects other responses', async () => {
  const api = parseSource('src/services/api.service.ts');
  const method = findSoleNode(
    api,
    'closeOperation method',
    (node) => ts.isMethodDeclaration(node) && node.name.getText(api) === 'closeOperation'
  );
  const source = transpile(
    `
    class CloseService {
      static getFetchOptions(options) { return options; }
      static async handleResponse(response) { throw new Error(String(response.status)); }
      ${method.getText(api)}
    }
  `,
    ts.ModuleKind.CommonJS
  );
  const calls = [];
  const responses = [
    { ok: true, status: 204 },
    { ok: false, status: 404 },
    { ok: false, status: 503 }
  ];
  const close = new Function('fetch', 'API_BASE', 'AbortSignal', `${source}\nreturn CloseService;`)(
    async (url, options) => {
      calls.push({ url, options });
      return responses.shift();
    },
    '/api',
    AbortSignal
  );
  await close.closeOperation('run-1');
  await close.closeOperation('run-2');
  await assert.rejects(close.closeOperation('run-3'), /503/);
  assert.deepEqual(
    calls.map((call) => call.url),
    ['/api/operations/run-1/close', '/api/operations/run-2/close', '/api/operations/run-3/close']
  );
  assert.equal(calls[0].options.method, 'POST');
  assert.equal(calls[0].options.headers['Content-Type'], 'application/json');
  assert.ok(calls[0].options.signal instanceof AbortSignal);
});
