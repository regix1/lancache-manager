import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
  compileToUrl,
  liftHookCallback,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * The Retro page fetched once and then showed that answer until someone changed a filter or
 * reloaded, so a download that finished afterwards, or a name resolved for one already listed,
 * never appeared. The hook behind it now subscribes to the shared refresh events and recovers on
 * reconnect, and that is what runs here: the hook is compiled and driven through real renders
 * against a fake hub, rather than described.
 */

const HOOK_PATH = 'src/components/features/downloads/useRetroDownloads.ts';
const RETRO_VIEW_PATH = 'src/components/features/downloads/RetroView.tsx';

/**
 * The smallest React that can run this hook: ordered slots for state, refs, callbacks and effects,
 * a re-render when state changes (including one raised from a timer after the render finished),
 * and cleanups on unmount.
 */
const reactStubSource = `
let slots = null;
let cursor = 0;
let queued = [];
let scheduleRender = null;

const depsChanged = (slot, deps) =>
  !slot.ran || deps.length !== slot.deps.length || deps.some((v, i) => !Object.is(v, slot.deps[i]));

export const useState = (initial) => {
  if (!slots[cursor]) {
    slots[cursor] = { value: typeof initial === 'function' ? initial() : initial };
  }
  const slot = slots[cursor++];
  const set = (next) => {
    const value = typeof next === 'function' ? next(slot.value) : next;
    if (Object.is(value, slot.value)) return;
    slot.value = value;
    scheduleRender();
  };
  return [slot.value, set];
};

export const useRef = (initial) => {
  if (!slots[cursor]) slots[cursor] = { current: initial };
  return slots[cursor++];
};

export const useCallback = (fn, deps) => {
  if (!slots[cursor]) slots[cursor] = { ran: false, deps: null, value: null };
  const slot = slots[cursor++];
  if (depsChanged(slot, deps)) {
    slot.ran = true;
    slot.deps = deps;
    slot.value = fn;
  }
  return slot.value;
};

export const useEffect = (run, deps) => {
  if (!slots[cursor]) slots[cursor] = { ran: false, deps: null, cleanup: null, next: null };
  const slot = slots[cursor++];
  if (depsChanged(slot, deps)) {
    slot.ran = true;
    slot.deps = deps;
    slot.next = run;
    queued.push(slot);
  }
};

export const createComponent = (body) => {
  const componentSlots = [];
  let rendering = false;
  let pending = false;

  const render = () => {
    if (rendering) {
      pending = true;
      return;
    }
    rendering = true;
    do {
      pending = false;
      slots = componentSlots;
      cursor = 0;
      queued = [];
      scheduleRender = render;
      body();
      const effects = queued;
      queued = [];
      slots = null;
      effects.forEach((slot) => {
        if (slot.cleanup) slot.cleanup();
        const cleanup = slot.next();
        slot.cleanup = typeof cleanup === 'function' ? cleanup : null;
      });
    } while (pending);
    rendering = false;
  };

  const unmount = () => {
    componentSlots.forEach((slot) => {
      if (slot && slot.cleanup) {
        slot.cleanup();
        slot.cleanup = null;
      }
    });
  };

  return { render, unmount };
};
`;

const signalRStubSource = `
export const hub = {
  isConnected: true,
  handlers: new Map(),
  emit(eventName) {
    (hub.handlers.get(eventName) || []).slice().forEach((handler) => handler());
  },
  subscribedNames() {
    return [...hub.handlers.entries()].filter(([, list]) => list.length > 0).map(([name]) => name);
  }
};

export const useSignalR = () => ({
  isConnected: hub.isConnected,
  on: (eventName, handler) => {
    hub.handlers.set(eventName, [...(hub.handlers.get(eventName) || []), handler]);
  },
  off: (eventName, handler) => {
    hub.handlers.set(
      eventName,
      (hub.handlers.get(eventName) || []).filter((entry) => entry !== handler)
    );
  }
});
`;

const apiStubSource = `
export const calls = [];
export const control = { hold: false, pending: [] };
const ApiService = {
  getRetroDownloads: (params, signal) => {
    calls.push({ ...params, signal });
    if (control.hold) {
      return new Promise((resolve, reject) => control.pending.push({ resolve, reject, signal }));
    }
    return Promise.resolve({
      items: [],
      totalItems: 0,
      totalPages: 0,
      currentPage: params.page,
      pageSize: params.pageSize
    });
  }
};
export default ApiService;
`;

const apiErrorStubSource = `
export class ApiError extends Error {
  constructor(status) {
    super('request failed');
    this.status = status;
  }
}
`;

const timeFilterStubSource = `
export const header = { timeRange: 'live' };
export const useTimeFilter = () => header;
`;

const reactUrl = moduleUrl(reactStubSource);
const signalRUrl = moduleUrl(signalRStubSource);
const apiUrl = moduleUrl(apiStubSource);
const apiErrorUrl = moduleUrl(apiErrorStubSource);
const timeFilterUrl = moduleUrl(timeFilterStubSource);

// The one refresh-rate setting every live surface reads. Held short so the test does not wait out a
// realistic cadence; the behaviour under test is that Retro reads THIS rather than a constant of
// its own.
const REFRESH_INTERVAL_MS = 200;
const refreshRateUrl = moduleUrl(`
export const useRefreshRate = () => ({ getRefreshInterval: () => ${REFRESH_INTERVAL_MS} });
`);

const eventsUrl = await compileToUrl('../src/contexts/SignalRContext/types.ts');

// Every case in this file is about the live path, so the toggle is off throughout and the
// generator is wired to fail loudly if the hook ever reaches for it here.
const mockModeUrl = moduleUrl(`
export const useMockMode = () => ({ mockMode: false });
`);
const mockDataUrl = moduleUrl(`
export default {
  generateMockRetroData() {
    throw new Error('the live path reached the mock generator');
  }
};
`);

const { createComponent } = await import(reactUrl);
const { hub } = await import(signalRUrl);
const { calls, control } = await import(apiUrl);
const { header } = await import(timeFilterUrl);
const { SIGNALR_REFRESH_EVENTS } = await import(eventsUrl);

/** Compiles the hook against the stubs above, so the code under test is the code that ships. */
const importHook = async (relativePath) =>
  import(
    await compileToUrl(relativePath, {
      react: reactUrl,
      '@services/api.service': apiUrl,
      '@services/apiError': apiErrorUrl,
      '@contexts/SignalRContext/types': eventsUrl,
      '@contexts/SignalRContext/useSignalR': signalRUrl,
      '@contexts/useTimeFilter': timeFilterUrl,
      '@contexts/useMockMode': mockModeUrl,
      '@/test/mockData.service': mockDataUrl,
      '@hooks/useReconnectRefetch': await compileToUrl('../src/hooks/useReconnectRefetch.ts', {
        react: reactUrl
      }),
      '@contexts/useRefreshRate': refreshRateUrl,
      '@hooks/useRefreshThrottle': await compileToUrl('../src/hooks/useRefreshThrottle.ts', {
        react: reactUrl
      })
    })
  );

const { useRetroDownloads } = await importHook(`../${HOOK_PATH}`);

const PROPS = {
  enabled: true,
  page: 1,
  pageSize: 50,
  sort: 'latest',
  service: 'all',
  client: 'all',
  search: '',
  hideLocalhost: false,
  hideMetadata: false,
  hideUnknown: false,
  hitMiss: 'all'
};

/**
 * A page of Retro, mounted fresh with an empty hub and no recorded requests behind it. Exposes the
 * hook's latest return value and a way to change its props, so a test can drive a user-initiated
 * dependency change the same way RetroView would (a new page, filter, or sort).
 */
const mountRetro = ({ isConnected = true, timeRange = 'live' } = {}) => {
  calls.length = 0;
  control.pending.length = 0;
  hub.handlers.clear();
  hub.isConnected = isConnected;
  header.timeRange = timeRange;
  let props = PROPS;
  let result;
  const component = createComponent(() => {
    result = useRetroDownloads(props);
  });
  component.render();
  return {
    ...component,
    getResult: () => result,
    setProps: (next) => {
      props = { ...props, ...next };
      component.render();
    }
  };
};

// Long enough to clear the interval the stub reports, by a margin.
const DEBOUNCE_WAIT_MS = REFRESH_INTERVAL_MS + 200;
const response = (items = [], page = 1) => ({
  items,
  totalItems: items.length,
  totalDownloads: items.length,
  totalPages: 1,
  currentPage: page,
  pageSize: 50
});

test('live events fold into one request after the request in flight settles', async () => {
  control.hold = true;
  const retro = mountRetro();
  assert.equal(calls.length, 1, 'the mount request is running');

  hub.emit('DownloadsRefresh');
  hub.emit('DownloadsRefresh');
  hub.emit('DownloadsRefresh');
  await delay(DEBOUNCE_WAIT_MS);
  assert.equal(calls.length, 1, 'events do not replace the running request');
  assert.equal(calls[0].signal.aborted, false);

  control.pending.shift().resolve(response());
  await delay(0);
  assert.equal(calls.length, 2, 'one follow-up starts after the held request settles');
  control.pending.shift().resolve(response());
  await delay(0);
  assert.equal(calls.length, 2, 'no further request starts without another event');

  retro.unmount();
  control.hold = false;
});

test('a page change and a reconnect still replace a request in flight', () => {
  control.hold = true;
  const pageChange = mountRetro();
  pageChange.setProps({ page: 2 });
  assert.equal(calls.length, 2);
  assert.equal(calls[0].signal.aborted, true, 'the page change aborts the old page');
  assert.equal(calls[1].page, 2);
  pageChange.unmount();

  const reconnect = mountRetro({ isConnected: false });
  hub.isConnected = true;
  reconnect.render();
  assert.equal(calls.length, 2);
  assert.equal(calls[0].signal.aborted, true, 'the reconnect replaces the unanswered request');
  assert.equal(calls[1].signal.aborted, false);
  reconnect.unmount();
  control.hold = false;
});

test('a failed refresh keeps rows and starts the follow-up queued during it', async () => {
  control.hold = true;
  const retro = mountRetro();
  const previous = { id: 'kept-row' };
  control.pending.shift().resolve(response([previous]));
  await delay(0);
  assert.deepEqual(retro.getResult().items, [previous]);

  hub.emit('DownloadsRefresh');
  assert.equal(calls.length, 2);
  hub.emit('DownloadsRefresh');
  await delay(DEBOUNCE_WAIT_MS);
  control.pending.shift().reject(new Error('server failure'));
  await delay(0);

  assert.deepEqual(retro.getResult().items, [previous], 'the previous response remains visible');
  assert.equal(retro.getResult().error?.message, 'server failure');
  assert.equal(calls.length, 3, 'the queued event starts one follow-up after failure');
  retro.unmount();
  control.hold = false;
});

test('a refresh event asks for the page again, with nothing on the page touched', async () => {
  const component = mountRetro();
  assert.equal(calls.length, 1, 'the mount fetch');
  // The fold keeps an event that arrives during the mount request for one follow-up. Let the
  // mount settle so this case continues to cover the idle leading refresh.
  await delay(0);

  hub.emit('DownloadsRefresh');
  assert.equal(
    calls.length,
    2,
    'the first event fetches on arrival: nothing has run for a whole interval, so a finished ' +
      'download must not sit behind a timer the other views do not wait out either'
  );
  const { signal: firstSignal, ...firstQuery } = calls[0];
  const { signal: secondSignal, ...secondQuery } = calls[1];
  assert.ok(firstSignal && secondSignal);
  assert.deepEqual(secondQuery, firstQuery, 'the same page and filters are asked for again');
  component.unmount();
});

test('a second event inside the interval waits, so a burst does not fetch per event', async () => {
  const component = mountRetro();
  // Let the mount request settle before exercising the throttle's leading and trailing calls.
  await delay(0);

  hub.emit('DownloadsRefresh');
  assert.equal(calls.length, 2, 'the first one goes straight out');

  hub.emit('DepotMappingComplete');
  assert.equal(calls.length, 2, 'the second is inside the interval, so it is held');

  await delay(DEBOUNCE_WAIT_MS);
  assert.equal(calls.length, 3, 'and lands once the interval has passed');
  component.unmount();
});

test('a name resolved after the fact reaches the page the same way', async () => {
  const component = mountRetro();

  hub.emit('DepotMappingComplete');
  await delay(DEBOUNCE_WAIT_MS);

  assert.equal(calls.length, 2, 'a row reading Unknown must not stay Unknown until a reload');
  component.unmount();
});

test('a burst of events arriving together does not fetch once per event', async () => {
  const component = mountRetro();

  hub.emit('DownloadsRefresh');
  hub.emit('DepotMappingComplete');
  hub.emit('GameRemovalComplete');
  await delay(DEBOUNCE_WAIT_MS);

  // The first goes straight out because nothing has run for a whole interval; the other two are
  // inside it and collapse into a single trailing reload. Three events, two reloads, which is what
  // the card, normal and compact views do with the same burst.
  assert.equal(calls.length, 3, 'the mount fetch, the leading reload, and one for the rest');
  component.unmount();
});

test('a connection that comes up after the mount fetch refetches', async () => {
  const component = mountRetro({ isConnected: false });
  assert.equal(calls.length, 1, 'the mount fetch went out with the socket down');

  hub.isConnected = true;
  component.render();

  assert.equal(calls.length, 2, 'events raised while the socket was down are never delivered');
  component.unmount();
});

test('on a bounded range the in-progress events are ignored, as the other views ignore them', async () => {
  const component = mountRetro({ timeRange: '24h' });

  hub.emit('DownloadsRefresh');
  hub.emit('LogProcessingComplete');
  await delay(DEBOUNCE_WAIT_MS);

  assert.equal(
    calls.length,
    1,
    'answering these off Live leaves Retro listing rows the card and compact views do not show'
  );
  component.unmount();
});

test('on a bounded range a completion still lands', async () => {
  const component = mountRetro({ timeRange: '24h' });

  hub.emit('GameRemovalComplete');
  await delay(DEBOUNCE_WAIT_MS);

  assert.equal(calls.length, 2, 'a removed game has to leave the screen on 24h too');
  component.unmount();
});

test('a background refresh leaves isFetching false, so the table does not fade', async () => {
  const retro = mountRetro();
  await delay(0);
  assert.equal(retro.getResult().isFetching, false, 'the mount fetch has to settle first');

  hub.emit('DownloadsRefresh');
  assert.equal(
    retro.getResult().isFetching,
    false,
    'a refreshVersion-only run is a background refresh; the fade must not show'
  );

  await delay(0);
  assert.equal(
    retro.getResult().isFetching,
    false,
    'and stays false once the background fetch settles'
  );
  retro.unmount();
});

test('a user-initiated page change still sets isFetching true', async () => {
  const retro = mountRetro();
  await delay(0);
  assert.equal(retro.getResult().isFetching, false, 'the mount fetch has to settle first');

  retro.setProps({ page: 2 });
  assert.equal(
    retro.getResult().isFetching,
    true,
    'a page change is user-initiated, so the fade must still show'
  );

  await delay(0);
  assert.equal(retro.getResult().isFetching, false, 'and clears once that fetch settles');
  retro.unmount();
});

test('the container never gains page-fading during a background refresh', async () => {
  const retro = mountRetro();
  await delay(0);
  assert.equal(retro.getResult().isFetching, false, 'the mount fetch has to settle first');

  // The real fade effect from RetroView.tsx, driven with the hook's own live result, so a bug in
  // either the hook or the component that reads it would show up here.
  const toggles = [];
  const fadeContainerRef = {
    current: { classList: { toggle: (className, fading) => toggles.push({ className, fading }) } }
  };
  const setPageFading = bindLifted(
    liftHookCallback(RETRO_VIEW_PATH, 'useCallback', "classList.toggle('page-fading'"),
    { fadeContainerRef }
  );

  hub.emit('DownloadsRefresh');
  bindLifted(
    liftHookCallback(RETRO_VIEW_PATH, 'useEffect', 'setPageFading(serverRetro.isFetching'),
    {
      serverMode: true,
      serverRetro: retro.getResult(),
      setPageFading
    }
  )();

  assert.deepEqual(
    toggles,
    [{ className: 'page-fading', fading: false }],
    'a background refresh must never toggle the container into page-fading'
  );
  retro.unmount();
});

test('a background refresh folds behind a page fetch and clears its fade after it settles', async () => {
  const retro = mountRetro();
  await delay(0);

  retro.setProps({ page: 2 });
  assert.equal(retro.getResult().isFetching, true, 'a page change is user-initiated, so it fades');

  // This event now folds instead of aborting the page request. The user-requested fade remains
  // until that page answers, and the queued background follow-up does not turn it back on.
  hub.emit('DownloadsRefresh');
  assert.equal(
    retro.getResult().isFetching,
    true,
    'the event does not abort the page request that owns the fade'
  );

  await delay(0);
  assert.equal(
    retro.getResult().isFetching,
    false,
    'the page settles and its queued background follow-up does not fade the table'
  );
  retro.unmount();
});

test('a reconnect while the view is hidden does not cost the next show its fade', async () => {
  const retro = mountRetro();
  await delay(0);

  retro.setProps({ enabled: false });

  // The reconnect refetch is not gated on enabled, so this bumps the version while the view sits
  // hidden behind display:none. Showing the view again is a user action and still has to fade.
  hub.isConnected = false;
  retro.setProps({});
  hub.isConnected = true;
  retro.setProps({});

  retro.setProps({ enabled: true });
  assert.equal(
    retro.getResult().isFetching,
    true,
    'showing the view again is user-initiated, whatever arrived while it was hidden'
  );

  await delay(0);
  assert.equal(retro.getResult().isFetching, false, 'and clears once that fetch settles');
  retro.unmount();
});

test('Retro answers the same events off Live that the other download views answer', () => {
  const retro = parseSource(HOOK_PATH);
  const liveOnly = collectNodes(
    retro,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(retro) === 'RETRO_LIVE_ONLY_EVENTS'
  )[0];
  assert.ok(liveOnly, 'the hook names the events it holds back off Live');
  const retroLiveOnly = liveOnly.initializer.elements.map((element) => element.text).sort();

  // The Dashboard's own list: everything in SIGNALR_REFRESH_EVENTS that its dedicatedHandlers block
  // does not claim still reaches handleRefreshEvent, which returns unless the range is 'live'.
  const dashboard = parseSource(
    'src/contexts/DashboardDataContext/index.tsx',
    typescript.ScriptKind.TSX
  );
  const dedicated = collectNodes(
    dashboard,
    (node) =>
      typescript.isVariableDeclaration(node) && node.name.getText(dashboard) === 'dedicatedHandlers'
  )[0];
  assert.ok(dedicated, 'the dashboard names the events it answers on every range');
  const answeredEverywhere = new Set(
    dedicated.initializer.properties.map((property) => property.name.getText(dashboard))
  );
  const dashboardLiveOnly = SIGNALR_REFRESH_EVENTS.filter(
    (name) => !answeredEverywhere.has(name)
  ).sort();

  assert.deepEqual(
    retroLiveOnly,
    dashboardLiveOnly,
    'the two sides disagreeing is what makes one time range show a download the other does not'
  );
});

test('unmounting removes every subscription it added', () => {
  const component = mountRetro();
  const subscribed = hub.subscribedNames();
  assert.ok(subscribed.length > 0, 'the hook subscribes to something');

  component.unmount();

  assert.deepEqual(
    hub.subscribedNames(),
    [],
    'off has to be given the same handler reference on was given, or the subscription leaks'
  );
});

test('the event list is checked against the event union, which on and off do not check', () => {
  const sourceFile = parseSource(HOOK_PATH);
  const [declaration] = collectNodes(
    sourceFile,
    (node) =>
      typescript.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === 'RETRO_REFRESH_EVENTS'
  );

  assert.ok(declaration, 'the hook declares the list it subscribes to');
  assert.equal(
    declaration.type?.getText(sourceFile),
    'readonly SignalREventName[]',
    'on and off take a plain string, so this annotation is the only thing catching a stale name'
  );
});
