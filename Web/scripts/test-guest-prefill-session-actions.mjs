import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import { bindLifted, findSoleNode, liftHookCallback, parseSource } from './transpile-module.mjs';

const path = 'src/components/features/management/sections/PrefillSessionsSection.tsx';
const source = parseSource(path, ts.ScriptKind.TSX);

// The popup title each action names when its request fails; the reason rides as its second line.
const FAILURE_TITLES = {
  handleTerminateSession: 'management.prefillSessions.errors.terminateSession',
  handleTerminateAll: 'management.prefillSessions.errors.terminateAll',
  handleBanBySession: 'management.prefillSessions.errors.banUser'
};

for (const action of ['handleTerminateSession', 'handleTerminateAll', 'handleBanBySession']) {
  test(`${action} refreshes committed partial results before releasing its retry control`, async () => {
    for (const failed of [true, false]) {
      const events = [];
      let release;
      const refresh = new Promise((resolve) => {
        release = resolve;
      });
      const failure = Object.assign(new Error('Access changed; cleanup is incomplete. Retry.'), {
        status: 503
      });
      const request = async () => {
        events.push('request');
        if (failed) throw failure;
        return { count: 2 };
      };
      const declaration = findSoleNode(
        source,
        action,
        (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === action
      );
      const run = bindLifted(declaration.initializer.getText(source), {
        setTerminatingSession: (value) => events.push(['terminating', value]),
        setTerminatingAll: (value) => events.push(['all', value]),
        setBanningSession: (value) => events.push(['banning', value]),
        setTerminateAllConfirm: (value) => events.push(['confirm', value]),
        setBanConfirm: (value) => events.push(['confirm', value]),
        banConfirm: { sessionId: 'guest-steam', reason: '' },
        ApiError: Error,
        ApiService: {
          terminatePrefillSession: request,
          terminateAllPrefillSessions: request,
          banPrefillUserBySession: request
        },
        onSuccess: (message) => events.push(['success', message]),
        onError: (message, error) => events.push(['error', message, error]),
        t: (key) => key,
        loadSessions: async () => {
          events.push('sessions');
          await refresh;
        },
        loadBans: async () => {
          events.push('bans');
          await refresh;
        }
      });
      const pending = run('guest-steam');
      await Promise.resolve();
      await Promise.resolve();
      assert.ok(events.includes('sessions'));
      assert.equal(events.includes('bans'), action === 'handleBanBySession');
      assert.equal(
        events.some((event) => Array.isArray(event) && event[0] === 'success'),
        !failed
      );
      assert.deepEqual(
        events.filter((event) => Array.isArray(event) && event[0] === 'error'),
        failed ? [['error', FAILURE_TITLES[action], failure]] : []
      );
      if (failed)
        assert.equal(
          events.some((event) => Array.isArray(event) && event[0] === 'confirm'),
          action !== 'handleTerminateSession'
        );
      const busy = events[0][0];
      assert.equal(events.filter((event) => Array.isArray(event) && event[0] === busy).length, 1);
      release();
      await pending;
      assert.deepEqual(events.at(-1), [busy, action === 'handleTerminateAll' ? false : null]);
    }
  });
}

// The session list load, with one session whose history read fails and one whose read succeeds.
const loadSessionsWith = ({ sessionsRequest, historyRequest, history, popups, sessionErrors }) => {
  const state = { history, sessions: [], loading: [] };
  const load = bindLifted(liftHookCallback(path, 'useCallback', 'Promise.allSettled'), {
    mockMode: false,
    page: 1,
    pageSize: 20,
    statusFilter: '',
    platformFilter: 'all',
    sessionsRequestRef: { current: 0 },
    ApiService: {
      getPrefillSessions: sessionsRequest,
      getActivePrefillSessions: async () => [],
      getPrefillSessionHistory: historyRequest
    },
    setLoadingSessions: (value) => state.loading.push(value),
    setSessionsError: (value) => sessionErrors.push(value),
    setSessions: (value) => state.sessions.push(value),
    setTotalCount: () => undefined,
    setActiveSessions: () => undefined,
    setCacheRoute: () => undefined,
    setHasLoadedSessions: () => undefined,
    setHistoryData: (update) => {
      state.history = update(state.history);
    },
    onError: (message) => popups.push(message),
    getErrorMessage: (error) => `reason: ${error.message}`
  });
  return { load, state };
};

test('a failed background history read keeps the last confirmed history', async () => {
  const popups = [];
  const sessionErrors = [];
  const { load, state } = loadSessionsWith({
    sessionsRequest: async () => ({
      sessions: [{ sessionId: 'fresh' }, { sessionId: 'kept' }],
      totalCount: 2,
      lastPrefillCacheIp: null,
      lastPrefillCacheIpSource: null
    }),
    historyRequest: async (sessionId) => {
      if (sessionId === 'kept') throw new Error('history read failed');
      return [{ appName: 'new' }];
    },
    history: { kept: [{ appName: 'confirmed' }] },
    popups,
    sessionErrors
  });

  await load();
  // The history reads settle after the list load returns.
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(state.history, {
    fresh: [{ appName: 'new' }],
    kept: [{ appName: 'confirmed' }]
  });
  assert.deepEqual(popups, []);
});

test('a failed session list load fills the section box and raises no popup', async () => {
  const popups = [];
  const sessionErrors = [];
  const { load } = loadSessionsWith({
    sessionsRequest: async () => {
      throw new Error('server down');
    },
    historyRequest: assert.fail,
    history: {},
    popups,
    sessionErrors
  });

  await load();

  assert.deepEqual(sessionErrors, [null, 'reason: server down']);
  assert.deepEqual(popups, [], 'the section box is the one place a failed load is shown');
});

// A read the test answers by hand, so an older request can be settled after a newer one.
const heldReads = () => {
  const reads = [];
  const request = () =>
    new Promise((resolve, reject) => {
      reads.push({ resolve, reject });
    });
  return { reads, request };
};

test('an older session list read that lands last never replaces the newer page', async () => {
  const sessionErrors = [];
  const { reads, request } = heldReads();
  const { load, state } = loadSessionsWith({
    sessionsRequest: request,
    historyRequest: async () => [],
    history: {},
    popups: [],
    sessionErrors
  });

  const older = load();
  const newer = load();
  reads[1].resolve({ sessions: [{ sessionId: 'newer-page' }], totalCount: 1 });
  await newer;
  reads[0].resolve({ sessions: [{ sessionId: 'older-page' }], totalCount: 1 });
  await older;

  assert.deepEqual(state.sessions, [[{ sessionId: 'newer-page' }]]);
  assert.equal(state.loading.at(-1), false);

  const failing = heldReads();
  const second = loadSessionsWith({
    sessionsRequest: failing.request,
    historyRequest: async () => [],
    history: {},
    popups: [],
    sessionErrors
  });
  const olderFailure = second.load();
  const newerSuccess = second.load();
  failing.reads[1].resolve({ sessions: [], totalCount: 0 });
  await newerSuccess;
  failing.reads[0].reject(new Error('stale read failed'));
  await olderFailure;

  assert.deepEqual(
    sessionErrors.filter((value) => value !== null),
    [],
    'the stale failure never reaches the section'
  );
});

test('an older load whose history prefetch lands last never replaces the newer history', async () => {
  const history = heldReads();
  const { load, state } = loadSessionsWith({
    sessionsRequest: async () => ({ sessions: [{ sessionId: 'session-s' }], totalCount: 1 }),
    historyRequest: history.request,
    history: {},
    popups: [],
    sessionErrors: []
  });

  // Each load publishes its list and leaves its history read running on its own.
  await load();
  await load();
  history.reads[1].resolve([{ appName: 'completed run' }]);
  await new Promise((resolve) => setImmediate(resolve));
  history.reads[0].resolve([]);
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(
    state.history,
    { 'session-s': [{ appName: 'completed run' }] },
    'the older empty answer would stay cached, and expanding the row would not read it again'
  );
});

// The count a section header shows, evaluated from the expression the component ships.
const headerCount = (titleKey, bindings) => {
  const section = findSoleNode(
    source,
    `${titleKey} section`,
    (node) =>
      ts.isJsxOpeningElement(node) &&
      node.tagName.getText(source) === 'AccordionSection' &&
      node.attributes.properties.some(
        (attribute) =>
          ts.isJsxAttribute(attribute) &&
          attribute.name.getText(source) === 'title' &&
          attribute.initializer.getText(source).includes(`'${titleKey}'`)
      )
  );
  const count = section.attributes.properties.find(
    (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(source) === 'count'
  );
  return bindLifted(`() => (${count.initializer.expression.getText(source)})`, bindings)();
};

const SECTION_TITLES = [
  'management.prefillSessions.liveSessions',
  'management.prefillSessions.persistentSessions.title',
  'management.prefillSessions.sessionHistory',
  'management.prefillSessions.bannedUsers.title'
];

test('a section header shows no count until its list has been read once', () => {
  const unread = {
    hasLoadedSessions: false,
    guestActiveSessions: [],
    scheduledPrefillConfig: null,
    persistentContainers: [],
    totalCount: 0,
    hasLoadedBans: false,
    activeBansCount: 0
  };
  for (const titleKey of SECTION_TITLES) {
    assert.equal(headerCount(titleKey, unread), undefined, `${titleKey} would claim 0 unread`);
  }

  const read = {
    hasLoadedSessions: true,
    guestActiveSessions: [{ id: 'a' }, { id: 'b' }],
    scheduledPrefillConfig: { version: 6 },
    persistentContainers: [{ sessionId: 'container-1' }],
    totalCount: 7,
    hasLoadedBans: true,
    activeBansCount: 3
  };
  assert.deepEqual(
    SECTION_TITLES.map((titleKey) => headerCount(titleKey, read)),
    [2, 1, 7, 3]
  );
});

test('an older bans read that fails after a newer one succeeded shows no error', async () => {
  const state = { bans: [], errors: [], loading: [], loaded: [] };
  const { reads, request } = heldReads();
  const loadBans = bindLifted(liftHookCallback(path, 'useCallback', 'getPrefillBans'), {
    bansRequestRef: { current: 0 },
    ApiService: { getPrefillBans: request },
    setLoadingBans: (value) => state.loading.push(value),
    setBansError: (value) => state.errors.push(value),
    setBans: (value) => state.bans.push(value),
    setHasLoadedBans: (value) => state.loaded.push(value),
    getErrorMessage: (error) => `reason: ${error.message}`
  });

  const older = loadBans();
  const newer = loadBans();
  reads[1].resolve([{ id: 1 }]);
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.bans, [[{ id: 1 }]]);
  assert.deepEqual(
    state.errors.filter((value) => value !== null),
    [],
    'the stale failure never reaches the section'
  );
  assert.equal(state.loading.at(-1), false);
  assert.deepEqual(state.loaded, [true], 'only the read that answered marks the list as read');
});

test('an older persistent containers read that fails after a newer one succeeded shows no error', async () => {
  const state = { containers: [], errors: [], loading: [] };
  const { reads, request } = heldReads();
  const loadPersistentContainers = bindLifted(
    liftHookCallback(path, 'useCallback', 'getPersistentPrefillContainers'),
    {
      persistentRequestRef: { current: 0 },
      ApiService: {
        getPersistentPrefillContainers: request,
        getScheduledPrefillConfig: async () => ({ version: 6 })
      },
      setLoadingPersistent: (value) => state.loading.push(value),
      setPersistentError: (value) => state.errors.push(value),
      setPersistentContainers: (value) => state.containers.push(value),
      setScheduledPrefillConfig: () => undefined,
      getErrorMessage: (error) => `reason: ${error.message}`
    }
  );

  const older = loadPersistentContainers();
  const newer = loadPersistentContainers();
  reads[1].resolve([{ sessionId: 'container-1' }]);
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.containers, [[{ sessionId: 'container-1' }]]);
  assert.deepEqual(
    state.errors.filter((value) => value !== null),
    [],
    'the stale failure never reaches the section'
  );
  assert.equal(state.loading.at(-1), false);
});

test('a failed history expand raises one popup and closes the row so the next expand fetches', async () => {
  const failure = new Error('history read failed');
  const popups = [];
  let expanded = new Set(['guest-steam', 'other']);
  let loading = new Set();
  let history = {};
  const loadHistory = bindLifted(
    liftHookCallback(
      path,
      'useCallback',
      'setLoadingHistory((prev) => new Set(prev).add(sessionId))'
    ),
    {
      ApiService: {
        getPrefillSessionHistory: async () => {
          throw failure;
        }
      },
      setLoadingHistory: (update) => {
        loading = update(loading);
      },
      setHistoryData: (update) => {
        history = update(history);
      },
      setExpandedHistory: (update) => {
        expanded = update(expanded);
      },
      onError: (message, error) => popups.push([message, error]),
      t: (key) => key
    }
  );

  await loadHistory('guest-steam');

  assert.deepEqual(popups, [['management.prefillSessions.errors.loadHistory', failure]]);
  assert.deepEqual([...expanded], ['other']);
  assert.deepEqual([...loading], []);
  assert.deepEqual(history, {}, 'no empty history is recorded, so the next expand fetches again');
});

test('one failed sessions read shows one box: History shows it only while Live Sessions is closed', () => {
  const historyBox = findSoleNode(
    source,
    'Session History error box',
    (node) =>
      ts.isBinaryExpression(node) &&
      node.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken &&
      node.right.getText(source).includes('<ErrorBlock') &&
      node.right.getText(source).includes('errors.loadHistory')
  );
  const shows = (liveSessionsExpanded) =>
    Boolean(
      bindLifted(`() => (${historyBox.left.getText(source)})`, {
        sessionsError: 'reason: server down',
        liveSessionsExpanded
      })()
    );

  assert.equal(shows(true), false, 'the open Live Sessions section already shows the box');
  assert.equal(shows(false), true);
});

test('the ban warning box carries no icon of its own under the dialog title icon', () => {
  const warning = findSoleNode(
    source,
    'ban warning box',
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(source) === 'Alert' &&
      node.getText(source).includes('modals.ban.warning')
  );
  const icon = warning.openingElement.attributes.properties.find(
    (attribute) => ts.isJsxAttribute(attribute) && attribute.name.getText(source) === 'icon'
  );
  assert.equal(icon?.initializer.expression.kind, ts.SyntaxKind.NullKeyword);
});

// The markup a section body renders, from the children the component ships. The body hides only
// when this is empty, so an empty wrapper div would leave a blank strip under the header.
const sectionBody = (titleKey, bindings) => {
  const section = findSoleNode(
    source,
    `${titleKey} section body`,
    (node) =>
      ts.isJsxElement(node) &&
      node.openingElement.tagName.getText(source) === 'AccordionSection' &&
      node.openingElement.attributes.properties.some(
        (attribute) =>
          ts.isJsxAttribute(attribute) &&
          attribute.name.getText(source) === 'title' &&
          attribute.initializer.getText(source).includes(`'${titleKey}'`)
      )
  );
  const children = section.children.map((child) => child.getText(source)).join('');
  return renderToStaticMarkup(
    bindLifted(`() => (<>${children}</>)`, { React, ...bindings }, { jsx: ts.JsxEmit.React })()
  );
};

// Every list failed and nothing was ever read. `ErrorBlock` is whatever the box renders.
const failedWithNoRows = (ErrorBlock) => ({
  ErrorBlock,
  t: (key) => key,
  loadingSessions: false,
  hasLoadedSessions: true,
  sessionsError: 'reason: server down',
  guestActiveSessions: [],
  sessions: [],
  liveSessionsExpanded: true,
  loadingPersistent: false,
  persistentError: 'reason: server down',
  persistentContainers: [],
  loadingBans: false,
  bansError: 'reason: server down',
  hasVisibleBans: false,
  loadSessions: () => undefined,
  loadPersistentContainers: () => undefined,
  loadBans: () => undefined,
  EnhancedDropdown: () => null,
  statusFilter: '',
  setStatusFilter: () => undefined,
  platformFilter: 'all',
  setPlatformFilter: () => undefined,
  setPage: () => undefined
});

test('under the connection banner a failed list with no rows leaves its section body empty', () => {
  // ErrorBlock renders nothing while the connection banner is up.
  const bodies = failedWithNoRows(function ErrorBlock() {
    return null;
  });

  assert.equal(sectionBody('management.prefillSessions.liveSessions', bodies), '');
  assert.equal(sectionBody('management.prefillSessions.persistentSessions.title', bodies), '');
  assert.equal(sectionBody('management.prefillSessions.bannedUsers.title', bodies), '');
  assert.equal(
    sectionBody('management.prefillSessions.sessionHistory', bodies),
    '<div class="prefill-history-filters"></div>',
    'History keeps its filters and adds no empty wrapper'
  );
});

test('while Live Sessions shows the failure, History adds no empty wrapper under its filters', () => {
  const bodies = failedWithNoRows(function ErrorBlock() {
    return React.createElement('div', { role: 'alert' });
  });

  assert.equal(
    sectionBody('management.prefillSessions.sessionHistory', bodies),
    '<div class="prefill-history-filters"></div>'
  );
  assert.equal(
    sectionBody('management.prefillSessions.liveSessions', bodies),
    '<div role="alert"></div>',
    'connected, the box is the whole body'
  );
});
