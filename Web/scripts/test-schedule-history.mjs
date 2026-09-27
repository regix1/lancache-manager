import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { renderToStaticMarkup } from 'react-dom/server';
import * as jsxRuntime from 'react/jsx-runtime';
import ts from 'typescript';
import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  parseSource,
  transpile
} from './transpile-module.mjs';

const componentPath = 'src/components/features/management/schedules/ScheduleHistory.tsx';
const historyWireText = String.raw`{"items":[{"id":4,"operationId":"44444444-4444-4444-4444-444444444444","serviceKey":"scheduledPrefill","status":"cancelled","trigger":"manual","actorKind":"account","accountId":"55555555-5555-5555-5555-555555555555","username":"history-owner","startedAt":"2026-09-27T13:03:00Z","completedAt":"2026-09-27T13:03:09Z","detail":"Cancelled after the active download stopped.","scheduleId":"66666666-6666-6666-6666-666666666666","scheduleName":"Nightly","platform":"Steam","workerStarted":true},{"id":3,"operationId":"33333333-3333-3333-3333-333333333333","serviceKey":"databaseBackup","status":"failed","actorKind":"unknown","startedAt":"2026-09-27T13:02:00Z","completedAt":"2026-09-27T13:02:08Z","detail":"The database did not answer before the backup deadline.","workerStarted":true},{"id":2,"operationId":"22222222-2222-2222-2222-222222222222","serviceKey":"cacheSizeScan","status":"skipped","trigger":"startup","actorKind":"server","startedAt":"2026-09-27T13:01:00Z","completedAt":"2026-09-27T13:01:01Z","workerStarted":false},{"id":1,"operationId":"11111111-1111-1111-1111-111111111111","serviceKey":"logRotation","status":"completed","trigger":"scheduled","actorKind":"server","startedAt":"2026-09-27T13:00:00Z","completedAt":"2026-09-27T13:00:05Z","workerStarted":true}],"page":1,"pageSize":100,"totalCount":4,"totalPages":1}`;
const historyWire = JSON.parse(historyWireText);
const fetchSource = liftHookCallback(componentPath, 'useCallback', 'ApiService.getScheduleHistory');
const changeQuerySource = liftHookCallback(
  componentPath,
  'useCallback',
  'nextQuery.page === current.page'
);
const changePageSource = liftHookCallback(
  componentPath,
  'useCallback',
  'changeQuery({ ...pageRef.current, page: nextPage })'
);

const [displayNames, rowToggles, platformConstants, notificationConstants, titleKeys] =
  await Promise.all(
    [
      '../src/utils/serviceDisplayName.ts',
      '../src/utils/rowToggle.ts',
      '../src/components/features/management/schedules/scheduled-prefill/constants.ts',
      '../src/contexts/notifications/constants.ts',
      '../src/contexts/notifications/notificationTitleKeys.ts'
    ].map(async (path) => import(await compileToUrl(path)))
  );

const localeValues = {
  en: JSON.parse(readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')),
  zh: JSON.parse(readFileSync(new URL('../src/i18n/locales/zh.json', import.meta.url), 'utf8'))
};

const readTranslation = (language, key) =>
  key.split('.').reduce((value, part) => value?.[part], localeValues[language]);

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

const query = (overrides = {}) => ({
  page: 1,
  pageSize: 20,
  search: '',
  serviceKey: '',
  status: '',
  ...overrides
});

const page = (number, operationId, overrides = {}) => ({
  items: [{ id: number, operationId }],
  page: number,
  pageSize: 20,
  totalCount: 60,
  totalPages: 3,
  ...overrides
});

const start = () => {
  const initialQuery = query();
  const state = {
    confirmed: null,
    confirmedQuery: null,
    query: initialQuery,
    error: null,
    loaded: false,
    expanded: new Set([1, 9]),
    requests: [],
    active: 0,
    maxActive: 0,
    mountedRef: { current: true },
    pageRef: { current: initialQuery },
    requestRef: {
      current: {
        running: false,
        pending: false,
        owner: 0,
        controller: null
      }
    }
  };
  const ApiService = {
    getScheduleHistory: (requestedPage, signal) => {
      const request = defer();
      request.query = { ...requestedPage };
      request.signal = signal;
      state.active++;
      state.maxActive = Math.max(state.maxActive, state.active);
      signal.addEventListener(
        'abort',
        () => {
          request.aborted = true;
          if (request.ignoreAbort) return;
          const error = new Error('aborted');
          error.name = 'AbortError';
          request.reject(error);
        },
        { once: true }
      );
      state.requests.push(request);
      return request.promise.finally(() => {
        state.active--;
      });
    }
  };
  const setExpandedIds = (value) => {
    state.expanded = typeof value === 'function' ? value(state.expanded) : value;
  };
  const fetchHistory = bindLifted(fetchSource, {
    requestRef: state.requestRef,
    pageRef: state.pageRef,
    mountedRef: state.mountedRef,
    ApiService,
    isAbortError: (error) => error?.name === 'AbortError',
    getErrorMessage: (error) => error.message,
    setQuery: (value) => {
      state.query = value;
    },
    setConfirmed: (value) => {
      state.confirmed = value;
    },
    setConfirmedQuery: (value) => {
      state.confirmedQuery = value;
    },
    setExpandedIds,
    setError: (value) => {
      state.error = value;
    },
    setLoaded: (value) => {
      state.loaded = value;
    }
  });
  const changeQuery = bindLifted(changeQuerySource, {
    pageRef: state.pageRef,
    requestRef: state.requestRef,
    setQuery: (value) => {
      state.query = value;
    },
    setExpandedIds,
    setError: (value) => {
      state.error = value;
    },
    fetchHistory
  });
  const changePage = bindLifted(changePageSource, {
    pageRef: state.pageRef,
    changeQuery
  });
  return { state, fetchHistory, changeQuery, changePage };
};

const apiFile = parseSource('src/services/api.service.ts');
const apiMethod = findSoleNode(
  apiFile,
  'getScheduleHistory method',
  (node) => ts.isMethodDeclaration(node) && node.name.getText(apiFile) === 'getScheduleHistory'
);
const apiArrow = `async (${apiMethod.parameters.map((parameter) => parameter.getText(apiFile)).join(', ')}) => ${apiMethod.body.getText(apiFile).replaceAll('this.', 'ApiService.')}`;

const callHistoryApi = async (requestedQuery) => {
  const calls = [];
  const getScheduleHistory = bindLifted(apiArrow, {
    API_BASE: '/api',
    ApiService: {
      getFetchOptions: ({ signal }) => ({ signal }),
      handleResponse: async (response) => response
    },
    fetch: async (url, options) => {
      calls.push({ url, options });
      return { items: [] };
    }
  });
  await getScheduleHistory(requestedQuery, new AbortController().signal);
  return calls;
};

const makeComponent = (capture, language = 'en') => {
  const sourceFile = parseSource(componentPath, ts.ScriptKind.TSX);
  let source = readFileSync(new URL(`../${componentPath}`, import.meta.url), 'utf8');
  const imports = sourceFile.statements
    .filter((statement) => ts.isImportDeclaration(statement))
    .sort((left, right) => right.getStart(sourceFile) - left.getStart(sourceFile));
  for (const declaration of imports) {
    source = source.slice(0, declaration.getStart(sourceFile)) + source.slice(declaration.getEnd());
  }
  const compiled = transpile(source, ts.ModuleKind.CommonJS, { jsx: ts.JsxEmit.ReactJSX });
  const values = [];
  const refs = [];
  let stateIndex = 0;
  let refIndex = 0;
  const useState = (initial) => {
    const index = stateIndex++;
    if (!(index in values)) values[index] = typeof initial === 'function' ? initial() : initial;
    const setValue = (value) => {
      values[index] = typeof value === 'function' ? value(values[index]) : value;
    };
    return [values[index], setValue];
  };
  const useRef = (initial) => {
    const index = refIndex++;
    if (!(index in refs)) refs[index] = { current: initial };
    return refs[index];
  };
  const t = (key, options) => {
    const value = readTranslation(language, key);
    if (typeof value !== 'string') return key;
    return value.replace(/\{\{(\w+)\}\}/g, (match, name) => options?.[name] ?? match);
  };
  const stub = (tag, name) => (props) => {
    capture[name]?.push(props);
    return jsxRuntime.jsx(tag, { children: props.children });
  };
  const bindings = {
    useCallback: (callback) => callback,
    useEffect: () => undefined,
    useRef,
    useState,
    useTranslation: () => ({
      t,
      i18n: { exists: (key) => typeof readTranslation(language, key) === 'string' }
    }),
    AccordionSection: (props) => {
      capture.accordions.push(props);
      return jsxRuntime.jsx('section', { children: props.children });
    },
    Badge: stub('span', 'badges'),
    CollapsibleRegion: (props) =>
      props.open
        ? jsxRuntime.jsx('div', { className: props.contentClassName, children: props.children })
        : null,
    ErrorBlock: stub('div', 'errors'),
    EnhancedDropdown: stub('button', 'dropdowns'),
    EmptyState: (props) => {
      capture.emptyStates.push(props);
      return jsxRuntime.jsx('p', { children: props.title });
    },
    LoadingState: stub('div', 'loaders'),
    Pagination: (props) => {
      capture.pagers.push(props);
      return jsxRuntime.jsx('nav', { children: props.currentPage });
    },
    SearchInput: (props) => {
      capture.searches.push(props);
      return jsxRuntime.jsx('input', { value: props.value, readOnly: true });
    },
    FormattedTimestamp: ({ timestamp }) => jsxRuntime.jsx('time', { children: timestamp }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY:
      notificationConstants.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY,
    NOTIFICATION_TITLE_KEYS: titleKeys.NOTIFICATION_TITLE_KEYS,
    useReconnectRefetch: () => undefined,
    ApiService: { getScheduleHistory: () => new Promise(() => undefined) },
    getErrorMessage: (error) => error.message,
    isAbortError: (error) => error?.name === 'AbortError',
    formatCount: (value) => value.toString(),
    rowToggleHandlers: (toggle) => {
      const handlers = rowToggles.rowToggleHandlers(toggle);
      capture.rows.push(handlers);
      return handlers;
    },
    formatServiceLabel: displayNames.formatServiceLabel,
    VARIANT_BY_STATUS: {
      completed: 'success',
      failed: 'danger',
      cancelled: 'neutral',
      skipped: 'warning'
    },
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY:
      platformConstants.SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
    SCHEDULE_HISTORY_PAGE_SIZE: 20
  };
  const module = { exports: {} };
  const names = Object.keys(bindings);
  new Function('module', 'exports', 'require', ...names, compiled)(
    module,
    module.exports,
    (name) => {
      if (name === 'react/jsx-runtime') return jsxRuntime;
      throw new Error(`Unexpected runtime import: ${name}`);
    },
    ...names.map((name) => bindings[name])
  );
  return {
    values,
    refs,
    render() {
      stateIndex = 0;
      refIndex = 0;
      for (const items of Object.values(capture)) items.length = 0;
      return renderToStaticMarkup(jsxRuntime.jsx(module.exports.ScheduleHistory, {}));
    }
  };
};

const renderHistory = (
  response,
  requestedQuery = query(),
  expanded = new Set(),
  language = 'en'
) => {
  const capture = {
    accordions: [],
    badges: [],
    dropdowns: [],
    emptyStates: [],
    errors: [],
    loaders: [],
    pagers: [],
    rows: [],
    searches: []
  };
  const component = makeComponent(capture, language);
  component.render();
  component.values[0] = true;
  component.values[1] = response;
  component.values[2] = requestedQuery;
  component.values[3] = true;
  component.values[5] = expanded;
  component.values[6] = requestedQuery;
  return { capture, component, html: component.render() };
};

test('history API sends every nonblank query field and omits blank optional fields', async () => {
  const blank = await callHistoryApi(query({ search: '   ' }));
  assert.equal(blank.length, 1);
  assert.equal(blank[0].url, '/api/system/schedules/history?page=1&pageSize=20');

  const filtered = await callHistoryApi(
    query({
      page: 4,
      pageSize: 100,
      search: '  cache run  ',
      serviceKey: 'epicMapping',
      status: 'failed'
    })
  );
  const url = new URL(filtered[0].url, 'http://history.test');
  assert.deepEqual(Object.fromEntries(url.searchParams), {
    page: '4',
    pageSize: '100',
    search: 'cache run',
    serviceKey: 'epicMapping',
    status: 'failed'
  });
});

test('ignored stale success drains one latest query and never overlaps requests', async () => {
  const { state, fetchHistory, changeQuery } = start();
  const initial = fetchHistory();
  state.requests[0].resolve(page(1, 'first'));
  await initial;

  const held = fetchHistory();
  state.requests[1].ignoreAbort = true;
  changeQuery(query({ pageSize: 50, search: 'new', serviceKey: 'epicMapping' }));
  assert.equal(state.requests[1].signal.aborted, true);
  state.requests[1].resolve(page(1, 'stale'));
  await settle();
  assert.equal(state.requests.length, 3);
  assert.deepEqual(
    state.requests[2].query,
    query({ pageSize: 50, search: 'new', serviceKey: 'epicMapping' })
  );
  assert.equal(state.confirmed.items[0].operationId, 'first');
  state.requests[2].resolve(page(1, 'latest', { pageSize: 50 }));
  await held;
  assert.equal(state.confirmed.items[0].operationId, 'latest');
  assert.equal(state.maxActive, 1);
});

test('ignored stale error and finalizer cannot publish or stop the replacement query', async () => {
  const { state, fetchHistory, changeQuery } = start();
  const held = fetchHistory();
  state.requests[0].ignoreAbort = true;
  changeQuery(query({ status: 'failed' }));
  state.requests[0].reject(new Error('stale failure'));
  await settle();
  assert.equal(state.error, null);
  assert.equal(state.requestRef.current.running, true);
  assert.equal(state.requests.length, 2);
  state.requests[1].resolve(page(1, 'filtered'));
  await held;
  assert.equal(state.confirmed.items[0].operationId, 'filtered');
  assert.equal(state.requestRef.current.running, false);
  assert.equal(state.requestRef.current.controller, null);
});

test('push and reconnect bursts keep one pending refresh and publish only the last owner', async () => {
  const { state, fetchHistory } = start();
  const held = fetchHistory();
  state.requests[0].ignoreAbort = true;
  await fetchHistory();
  await fetchHistory();
  assert.equal(state.requestRef.current.pending, true);
  assert.equal(state.requests.length, 1);
  state.requests[0].resolve(page(1, 'stale'));
  await settle();
  assert.equal(state.requests.length, 2);
  state.requests[1].resolve(page(1, 'latest'));
  await held;
  assert.equal(state.confirmed.items[0].operationId, 'latest');
  assert.equal(state.maxActive, 1);
});

test('server clamp is accepted without a second request and refresh prunes expansions', async () => {
  const { state, changeQuery, fetchHistory } = start();
  const requested = query({
    page: 9,
    pageSize: 100,
    search: 'run',
    serviceKey: 'gameDetection',
    status: 'completed'
  });
  changeQuery(requested);
  assert.deepEqual(state.requests[0].query, requested);
  state.requests[0].resolve(
    page(2, 'clamped', {
      pageSize: 100,
      items: [{ id: 9, operationId: 'clamped' }],
      totalPages: 2
    })
  );
  await settle();
  assert.equal(state.requests.length, 1);
  assert.equal(state.pageRef.current.page, 2);
  assert.equal(state.query.page, 2);
  assert.equal(state.expanded.size, 0);
  assert.equal(state.confirmedQuery.status, 'completed');

  state.expanded = new Set([9, 10]);
  const refresh = fetchHistory();
  state.requests[1].resolve(
    page(2, 'current', {
      pageSize: 100,
      items: [{ id: 9, operationId: 'current' }],
      totalCount: 1,
      totalPages: 1
    })
  );
  await refresh;
  assert.deepEqual([...state.expanded], [9]);

  const emptyRefresh = fetchHistory();
  state.requests[2].resolve(
    page(2, 'empty', { pageSize: 100, items: [], totalCount: 0, totalPages: 0 })
  );
  await emptyRefresh;
  assert.equal(state.confirmed.items.length, 0);
  assert.equal(state.expanded.size, 0);
});

test('page changes preserve the complete query, reset expansion, and ignore the current page', async () => {
  const { state, changePage } = start();
  state.pageRef.current = query({
    page: 2,
    pageSize: 50,
    search: 'user',
    serviceKey: 'epicMapping',
    status: 'failed'
  });
  state.query = state.pageRef.current;
  changePage(2);
  assert.equal(state.requests.length, 0);
  changePage(3);
  assert.deepEqual(
    state.pageRef.current,
    query({
      page: 3,
      pageSize: 50,
      search: 'user',
      serviceKey: 'epicMapping',
      status: 'failed'
    })
  );
  assert.equal(state.expanded.size, 0);
  assert.equal(state.requests.length, 1);
  state.requests[0].resolve(page(3, 'third', { pageSize: 50 }));
  await settle();
});

test('refresh error keeps rows, retry clears it, and unmount blocks publication', async () => {
  const { state, fetchHistory } = start();
  const initial = fetchHistory();
  state.requests[0].resolve(page(1, 'confirmed'));
  await initial;

  const failed = fetchHistory();
  state.requests[1].reject(new Error('refresh failed'));
  await failed;
  assert.equal(state.confirmed.items[0].operationId, 'confirmed');
  assert.equal(state.error, 'refresh failed');

  const retried = fetchHistory();
  state.requests[2].resolve(page(1, 'recovered'));
  await retried;
  assert.equal(state.confirmed.items[0].operationId, 'recovered');
  assert.equal(state.error, null);

  const unmounted = fetchHistory();
  state.requests[3].ignoreAbort = true;
  state.mountedRef.current = false;
  state.requestRef.current.pending = false;
  state.requestRef.current.controller.abort();
  state.requests[3].resolve(page(1, 'after-unmount'));
  await unmounted;
  assert.equal(state.confirmed.items[0].operationId, 'recovered');
});

test('wire omissions render fallback titles without raw keys or blank detail surfaces', () => {
  const startedAt = '2026-09-27T10:00:00Z';
  const completedAt = '2026-09-27T10:00:05Z';
  const items = [
    {
      id: 1,
      operationId: 'missing',
      serviceKey: 'xboxlive',
      status: 'completed',
      startedAt,
      completedAt,
      workerStarted: true,
      actorKind: 'server'
    },
    {
      id: 2,
      operationId: 'empty',
      serviceKey: 'gameDetection',
      status: 'failed',
      startedAt,
      completedAt,
      workerStarted: true,
      actorKind: 'unknown',
      scheduleName: '',
      trigger: '',
      detail: ''
    },
    {
      id: 3,
      operationId: 'space',
      serviceKey: 'gameDetection',
      status: 'cancelled',
      startedAt,
      completedAt,
      workerStarted: true,
      actorKind: 'server',
      scheduleName: '   ',
      trigger: '   ',
      detail: '   ',
      platform: 'steam'
    },
    {
      id: 4,
      operationId: 'valid',
      serviceKey: 'gameDetection',
      status: 'skipped',
      startedAt,
      completedAt,
      workerStarted: true,
      actorKind: 'account',
      accountId: 'A1',
      username: 'Ada',
      scheduleName: 'Captured name',
      trigger: 'runAll',
      detail: 'first line\nsecond line',
      platform: 'Steam'
    },
    {
      id: 5,
      operationId: 'unsupported',
      serviceKey: 'mysteryService',
      status: 'completed',
      startedAt,
      completedAt,
      workerStarted: true,
      actorKind: 'server',
      platform: 'Other'
    }
  ];
  const response = { items, page: 1, pageSize: 20, totalCount: items.length, totalPages: 1 };
  const { html } = renderHistory(response, query(), new Set(items.map((item) => item.id)));
  assert.doesNotMatch(html, /schedule-history-name"><\/span>/);
  assert.doesNotMatch(html, /\.undefined|triggers\.(?:undefined|null)|config\.services\.undefined/);
  assert.doesNotMatch(html, /schedule-history-terminal-detail"><\/p>/);
  assert.match(html, /Xbox/);
  assert.match(html, /Game Detection/);
  assert.match(html, /Captured name/);
  assert.match(html, /MysteryService/);
  assert.equal((html.match(/schedule-history-terminal-detail/g) ?? []).length, 1);
  assert.equal((html.match(/>Steam</g) ?? []).length, 1);
  assert.match(html, /first line\nsecond line/);
});

test('the exact endpoint response renders all raw items without sanitizing omitted fields', () => {
  assert.equal(
    createHash('sha256').update(historyWireText).digest('hex').toUpperCase(),
    '97F13E4CA9DF9432C078ADD24BC79F8CFB0FFB303ADF1BE3CB2F471A14AB3EF7'
  );
  const expanded = new Set(historyWire.items.map((item) => item.id));
  const { html } = renderHistory(historyWire, query({ pageSize: 100 }), expanded);
  assert.equal(historyWire.items.length, 4);
  assert.equal(historyWire.pageSize, 100);
  assert.match(html, /Nightly/);
  assert.match(html, /history-owner/);
  assert.match(html, /DatabaseBackup/);
  assert.match(html, /Cache File Scan/);
  assert.match(html, /Log Rotation/);
  assert.match(html, /Cancelled after the active download stopped\./);
  assert.match(html, /The database did not answer before the backup deadline\./);
  assert.doesNotMatch(html, /schedule-history-name"><\/span>|\.undefined/);
  assert.equal((html.match(/schedule-history-terminal-detail/g) ?? []).length, 2);
  assert.equal((html.match(/>Steam</g) ?? []).length, 1);
});

test('all fourteen scheduled services share meaningful localized dropdown and row labels', () => {
  const services = [
    ...new Set(
      Object.values(notificationConstants.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY).filter(
        (serviceKey) => typeof serviceKey === 'string'
      )
    )
  ];
  assert.equal(services.length, 14);
  const startedAt = '2026-09-27T10:00:00Z';
  const completedAt = '2026-09-27T10:00:05Z';
  const response = {
    items: services.map((serviceKey, index) => ({
      id: index + 1,
      operationId: `known-${index + 1}`,
      serviceKey,
      status: 'completed',
      actorKind: 'server',
      startedAt,
      completedAt,
      workerStarted: true
    })),
    page: 1,
    pageSize: 20,
    totalCount: services.length,
    totalPages: 1
  };

  for (const language of ['en', 'zh']) {
    const { capture, html } = renderHistory(response, query(), new Set(), language);
    const options = new Map(
      capture.dropdowns[0].options.map((option) => [option.value, option.label])
    );
    assert.equal(options.size, 15);
    for (const serviceKey of services) {
      const displayNameKey = `management.schedules.services.${serviceKey}.displayName`;
      const scheduledNotification = Object.entries(
        notificationConstants.SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY
      ).find(([, scheduledServiceKey]) => scheduledServiceKey === serviceKey);
      const titleKey = titleKeys.NOTIFICATION_TITLE_KEYS[scheduledNotification[0]];
      const displayName = readTranslation(language, displayNameKey);
      const expected =
        typeof displayName === 'string' ? displayName : readTranslation(language, titleKey);
      assert.equal(options.get(serviceKey), expected);
      assert.notEqual(options.get(serviceKey), serviceKey);
      assert.match(html, new RegExp(expected.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    }
  }
});

test('old omission branches render the blank title, undefined key, and empty detail control', () => {
  const execution = {
    scheduleName: undefined,
    platform: undefined,
    detail: undefined,
    serviceKey: 'gameDetection'
  };
  const serviceName = execution.scheduleName !== null ? execution.scheduleName : 'Game Detection';
  const oldPlatform = `management.schedules.services.scheduledPrefill.config.services.${platformConstants.SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[execution.platform]}`;
  const html = renderToStaticMarkup(
    jsxRuntime.jsxs('div', {
      children: [
        jsxRuntime.jsx('span', { className: 'schedule-history-name', children: serviceName }),
        execution.platform !== null && jsxRuntime.jsx('span', { children: oldPlatform }),
        execution.detail !== null &&
          jsxRuntime.jsx('p', {
            className: 'schedule-history-terminal-detail',
            children: execution.detail
          })
      ]
    })
  );
  assert.match(html, /schedule-history-name"><\/span>/);
  assert.match(html, /config\.services\.undefined/);
  assert.match(html, /schedule-history-terminal-detail"><\/p>/);
});

test('rendered controls publish search, service, status, size, page, and row changes', () => {
  const response = page(1, 'one', {
    items: [
      {
        id: 1,
        operationId: 'one',
        serviceKey: 'gameDetection',
        status: 'completed',
        trigger: null,
        startedAt: '2026-09-27T10:00:00Z',
        completedAt: '2026-09-27T10:00:05Z',
        workerStarted: true,
        actorKind: 'server'
      }
    ],
    totalCount: 60,
    totalPages: 3
  });
  const { capture, component } = renderHistory(response);
  assert.equal(capture.dropdowns.length, 3);
  assert.equal(capture.dropdowns[0].options.length, 15);
  assert.equal(new Set(capture.dropdowns[0].options.map((option) => option.value)).size, 15);
  assert.deepEqual(
    capture.dropdowns[2].options.map((option) => option.value),
    ['20', '50', '100']
  );
  assert.equal(capture.pagers[0].currentPage, 1);
  assert.equal(capture.pagers[0].totalItems, 60);
  assert.equal(capture.pagers[0].itemsPerPage, 20);

  capture.searches[0].onChange({ target: { value: 'needle' } });
  assert.equal(component.values[6].search, 'needle');
  assert.equal(component.values[6].page, 1);
  assert.equal(component.values[5].size, 0);

  capture.dropdowns[0].onChange('epicMapping');
  assert.equal(component.values[6].serviceKey, 'epicMapping');
  capture.dropdowns[1].onChange('failed');
  assert.equal(component.values[6].status, 'failed');
  capture.dropdowns[2].onChange('100');
  assert.equal(component.values[6].pageSize, 100);
  capture.dropdowns[2].onChange('10');
  assert.equal(component.values[6].pageSize, 100);
  capture.pagers[0].onPageChange(2);
  assert.equal(component.values[6].page, 2);

  const OriginalElement = globalThis.Element;
  const OriginalHtmlElement = globalThis.HTMLElement;
  class TestElement {
    closest() {
      return null;
    }
  }
  class TestHtmlElement extends TestElement {}
  globalThis.Element = TestElement;
  globalThis.HTMLElement = TestHtmlElement;
  const row = new TestHtmlElement();
  try {
    capture.rows[0].onClick({ target: row, currentTarget: row });
    assert.deepEqual([...component.values[5]], [1]);
    capture.rows[0].onKeyDown({
      key: 'Enter',
      target: row,
      currentTarget: row,
      preventDefault: () => undefined
    });
    assert.equal(component.values[5].size, 0);
  } finally {
    globalThis.Element = OriginalElement;
    globalThis.HTMLElement = OriginalHtmlElement;
  }
});

test('filtered and unfiltered confirmed empty results use the matching message', () => {
  const empty = { items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 };
  const unfiltered = renderHistory(empty);
  assert.equal(unfiltered.capture.emptyStates[0].title, 'No schedule runs have finished yet.');
  assert.equal(unfiltered.capture.accordions[0].count, 0);
  assert.equal(unfiltered.capture.searches.length, 1);

  const filtered = renderHistory(empty, query({ search: 'none' }));
  assert.equal(filtered.capture.emptyStates[0].title, 'No runs match your search or filters.');
  assert.equal(filtered.capture.dropdowns.length, 3);
});

test('the desktop page-size trigger fits every measured English and Chinese amount label', () => {
  const css = readFileSync(
    new URL(
      '../src/components/features/management/schedules/SchedulesSection.css',
      import.meta.url
    ),
    'utf8'
  );
  const sizeRule = css.match(/\.schedule-history-filter--size\s*\{([^}]+)\}/)?.[1];
  assert.match(sizeRule, /flex:\s*0 1 10\.5rem/);
  assert.match(sizeRule, /min-width:\s*10\.5rem/);
  assert.match(css, /\.schedule-history-filter\s*\{[^}]*flex:\s*1 1 100%/s);
  assert.match(
    css,
    /@media \(max-width: 639px\)[\s\S]*\.schedule-history-filter \.ed-trigger\s*\{[^}]*min-height:\s*2\.75rem/
  );

  const triggerWidth = 10.5 * 16;
  const fixedWidth = 24 + 16 + 6 + 2;
  for (const labelWidth of [108, 108, 115, 90, 90, 96]) {
    assert.ok(labelWidth + fixedWidth <= triggerWidth);
  }
});

test('the component binds the same refetch to schedule pushes and reconnects', () => {
  const source = parseSource(componentPath, ts.ScriptKind.TSX).getFullText();
  assert.match(source, /on\('SchedulesUpdated', handleSchedulesUpdated\)/);
  assert.match(source, /useReconnectRefetch\(isConnected, fetchHistory\)/);
  assert.match(source, /confirmed && confirmed\.items\.length > 0/);
  assert.match(source, /!loaded && <LoadingState/);
  assert.match(source, /schedule-history-list mgmt-list divided-list/);
});
