import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  MemoryStorage,
  bindLifted,
  collectNodes,
  compileToUrl,
  declaredValue,
  findSoleNode,
  liftConstArrow,
  liftHookCallback,
  parseSource
} from './transpile-module.mjs';

/**
 * A field stays editable while its form saves, so a change can land between the click and the
 * answer. These cases run each shipped save handler against a request held open, make the change
 * while it waits, and check the change is neither dropped nor reported as saved.
 */

/** A promise the test settles by hand, standing in for a request that is still on the wire. */
const deferred = () => {
  let resolve;
  let reject;
  const promise = new Promise((done, fail) => {
    resolve = done;
    reject = fail;
  });
  return { promise, resolve, reject };
};

/** Lets every pending `.then`/`await` continuation run. */
const settle = () => new Promise((done) => setTimeout(done, 0));

/** A React state slot whose setter takes a value or an updater, as `useState`'s does. */
const slot = (initial) => {
  const box = { value: initial };
  box.set = (next) => {
    box.value = typeof next === 'function' ? next(box.value) : next;
  };
  return box;
};

/** The persistent prefill settings Save, opened on 2 days with 3 typed and its write held open. */
const settingsSaveHarness = () => {
  const put = deferred();
  const calls = { closed: 0 };
  const saved = { current: { days: 2, mode: 'killOnRestart' } };
  const dirty = { current: { days: true, mode: false } };
  const revisions = { current: { days: 0, mode: 0, clear: 0 } };
  const editedDays = { current: 3 };
  const save = bindLifted(
    liftConstArrow(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillSharedSettingsModal.tsx',
      'save'
    ),
    {
      saving: false,
      clearing: false,
      session: { current: { opened: true, id: 1 } },
      clearPending: false,
      setClearing: () => undefined,
      setClearOutcome: () => undefined,
      containers: { loadPersistentContainers: () => undefined },
      t: (key) => key,
      baseKey: 'base',
      setClearPending: () => undefined,
      getErrorMessage: (error) => String(error),
      setClearOpen: () => undefined,
      setSaving: () => undefined,
      days: 3,
      saved,
      dirty,
      revisions,
      editedDays,
      daysWrite: { current: { serial: 0, pending: 0 } },
      modeWrite: { current: { serial: 0, pending: 0 } },
      ApiService: { updatePersistentPrefillValidity: () => put.promise },
      setReadErrors: () => undefined,
      setSaveErrors: () => undefined,
      setDays: () => undefined,
      mode: 'killOnRestart',
      setMode: () => undefined,
      notifyError: () => undefined,
      onClose: () => {
        calls.closed += 1;
      }
    }
  );
  // What the number box's onChange does with a value typed while the write runs.
  const typeDays = (value) => {
    revisions.current.days += 1;
    editedDays.current = value;
    dirty.current.days = value !== saved.current.days;
  };
  return { put, calls, saved, dirty, revisions, save, typeDays };
};

test('persistent prefill settings: days set back during the save keep the dialog open', async () => {
  const settings = settingsSaveHarness();
  const running = settings.save();
  settings.typeDays(2);
  settings.put.resolve();
  await running;

  assert.equal(settings.calls.closed, 0, 'the dialog must not close over an unsaved edit');
  assert.equal(settings.saved.current.days, 3, 'the server now holds the sent days');
  assert.equal(settings.dirty.current.days, true, 'the shown 2 differs from the stored 3');
});

test('persistent prefill settings: days typed back to the sent value during the save close the dialog', async () => {
  const settings = settingsSaveHarness();
  const running = settings.save();
  settings.typeDays(5);
  settings.typeDays(3);
  settings.put.resolve();
  await running;

  assert.equal(settings.saved.current.days, 3);
  assert.equal(settings.dirty.current.days, false, 'the shown 3 is what the server stored');
  assert.equal(settings.calls.closed, 1);
});

test('persistent prefill settings: a clear switched on during the save keeps the dialog open', async () => {
  const settings = settingsSaveHarness();
  const running = settings.save();
  // The clear-logins switch's onChange.
  settings.revisions.current.clear += 1;
  settings.put.resolve();
  await running;

  assert.equal(settings.calls.closed, 0, 'the clear choice made during the save was never run');
});

const clientGroupModal = 'src/components/modals/ClientGroupModal.tsx';

test('client nickname: addresses changed during the save keep the dialog open, each shown once', async () => {
  const members = deferred();
  const calls = { closed: 0, success: 0 };
  const removed = slot([]);
  const chosen = slot(new Set(['10.0.0.3']));
  const chosenSet = chosen.value;
  const editsRef = {
    current: {
      nickname: 'Den',
      description: '',
      separateMemberRows: false,
      removedIps: removed.value,
      chosen: chosenSet
    }
  };
  const handleSubmit = bindLifted(
    liftHookCallback(clientGroupModal, 'useCallback', 'updateClientGroup(savedGroupId'),
    {
      t: (key) => key,
      nickname: 'Den',
      description: '',
      separateMemberRows: false,
      pendingMemberIps: ['10.0.0.1', '10.0.0.2', '10.0.0.3'],
      currentMemberIps: ['10.0.0.1', '10.0.0.2'],
      removedIps: removed.value,
      chosenList: ['10.0.0.3'],
      chosen: { selected: chosenSet },
      group: { id: 7, memberIps: ['10.0.0.1', '10.0.0.2'] },
      savedGroupId: 7,
      editsRef,
      expectedUpdatedAtRef: { current: 'a' },
      updateClientGroup: async () => ({ status: 'ok', updatedAtUtc: 'b' }),
      setMembers: () => members.promise,
      createClientGroup: async () => {
        throw new Error('not a create');
      },
      setChosenMany: (ips, selected) =>
        chosen.set((previous) => {
          const next = new Set(previous);
          for (const ip of ips) {
            if (selected) next.add(ip);
            else next.delete(ip);
          }
          return next;
        }),
      clearChosen: () => chosen.set(new Set()),
      setRemovedIps: removed.set,
      setCreatedGroupId: () => undefined,
      reseedFromServerCopy: () => undefined,
      setError: () => undefined,
      setRejectedIps: () => undefined,
      setSaving: () => undefined,
      console,
      getErrorMessage: (error) => String(error),
      onSuccess: () => {
        calls.success += 1;
      },
      onClose: () => {
        calls.closed += 1;
      }
    }
  );
  const running = handleSubmit({ preventDefault: () => undefined });
  await settle();
  // While the membership request waits: the new chip is taken back off and .1 is marked removed.
  editsRef.current = { ...editsRef.current, removedIps: ['10.0.0.1'], chosen: new Set() };
  members.resolve({ status: 'ok', group: { updatedAtUtc: 'c' }, rejectedIps: [] });
  await running;

  assert.equal(calls.closed, 0, 'the dialog must not close over an unsaved edit');
  // The server now holds .1 .2 .3; the person wants only .2.
  assert.deepEqual([...removed.value].sort(), ['10.0.0.1', '10.0.0.3']);
  assert.deepEqual([...chosen.value], []);
});

test('user accounts: Disable on a second account while the first one runs sends its request', async () => {
  const requests = [];
  const pending = deferred();
  const setDisabled = bindLifted(
    liftConstArrow('src/components/features/user/useUserAccounts.ts', 'setAccountDisabled'),
    {
      setDisabledInProgressRef: { current: new Set() },
      setDisablingAccountId: () => undefined,
      fetch: (url) => {
        requests.push(url);
        return pending.promise;
      },
      API_BASE: '/api',
      ApiService: {
        getJsonFetchOptions: () => ({}),
        handleResponse: async () => ({ id: 'x' })
      },
      setAccounts: () => undefined,
      notifyError: () => undefined,
      t: (key) => key
    }
  );
  const first = setDisabled({ id: 'a' }, true);
  const second = setDisabled({ id: 'b' }, true);
  const repeat = setDisabled({ id: 'a' }, true);
  pending.resolve({});
  await Promise.all([first, second, repeat]);

  assert.deepEqual(requests, ['/api/accounts/a/disabled', '/api/accounts/b/disabled']);
});

test('user accounts: a Disable ending on another account leaves the running delete busy; a repeat confirm is ignored', async () => {
  const requests = [];
  const answers = [];
  const busy = slot(null);
  const disablingAccount = slot(null);
  const bindings = {
    setDisabledInProgressRef: { current: new Set() },
    deleteInProgressRef: { current: false },
    setBusyAccountId: busy.set,
    setDisablingAccountId: disablingAccount.set,
    fetch: (url, options) => {
      requests.push([options.method, url]);
      const answer = deferred();
      answers.push(answer);
      return answer.promise;
    },
    API_BASE: '/api',
    ApiService: {
      getJsonFetchOptions: (_body, options) => options,
      getFetchOptions: (options) => options,
      handleResponse: async () => ({ id: 'a' })
    },
    setAccounts: () => undefined,
    onConfirmationClose: () => undefined,
    setDeleteError: () => undefined,
    getErrorMessage: (error) => String(error),
    notifyError: () => undefined,
    t: (key) => key
  };
  const setDisabled = bindLifted(
    liftConstArrow('src/components/features/user/useUserAccounts.ts', 'setAccountDisabled'),
    bindings
  );
  const deleteAccount = bindLifted(
    liftConstArrow('src/components/features/user/useUserAccounts.ts', 'deleteAccount'),
    bindings
  );
  const runConfirmation = bindLifted(
    liftConstArrow('src/components/features/user/AccountDialogs.tsx', 'runConfirmation'),
    { ...bindings, deleteAccount }
  );

  const disabling = setDisabled({ id: 'a' }, true);
  const deleting = runConfirmation({ account: { id: 'b' } });
  const repeat = runConfirmation({ account: { id: 'b' } });
  answers[0].resolve({});
  await disabling;

  assert.equal(busy.value, 'b', 'the delete dialog must stay busy while its request runs');
  assert.equal(disablingAccount.value, null);
  assert.deepEqual(requests, [
    ['PUT', '/api/accounts/a/disabled'],
    ['DELETE', '/api/accounts/b']
  ]);
  answers[1].resolve({});
  await Promise.all([deleting, repeat]);
  assert.equal(busy.value, null);
});

test('access setup: a field changed while Save runs keeps the page on the form with a notice', async () => {
  const configure = deferred();
  const calls = { closed: 0, tests: 0, errors: [] };
  const values = { mode: 'password', apiKey: 'key', displayName: 'Old' };
  const formValuesRef = { current: values };
  const save = bindLifted(liftConstArrow('src/components/initialization/AccessSetup.tsx', 'save'), {
    busy: false,
    modeBlocked: false,
    apiKey: 'key',
    setError: (message) => calls.errors.push(message),
    setWipeError: () => undefined,
    setWipeCount: () => undefined,
    t: (key) => key,
    needsLocalPassword: false,
    validateAccountCredentials: () => ({}),
    localUsername: '',
    localPassword: '',
    localConfirm: '',
    setLocalErrors: () => undefined,
    setBusy: () => undefined,
    needsSignIn: false,
    ownerSso: false,
    ownerService: '',
    authService: { configureAccess: () => configure.promise },
    window: { location: { assign: () => undefined } },
    login: async () => ({ success: true }),
    setPassword: () => undefined,
    password: '',
    resumeTest: false,
    pendingTest: false,
    changeStep: () => undefined,
    requestAnimationFrame: () => undefined,
    heading: { current: null },
    setLocalPassword: () => undefined,
    setLocalConfirm: () => undefined,
    authenticationSetupRequired: false,
    signInModeFromEnvironment: false,
    oidc: false,
    credentialsNeeded: false,
    selectedConnected: undefined,
    startTest: async () => {
      calls.tests += 1;
    },
    allowedSubjects: '',
    mode: 'password',
    acknowledged: false,
    kind: 'google',
    setByEnvironment: () => false,
    clientId: '',
    needsClientSecret: () => false,
    clientSecret: '',
    displayName: 'Old',
    tenant: '',
    authority: '',
    teamId: '',
    keyId: '',
    privateKey: '',
    setClientSecret: () => undefined,
    setPrivateKey: () => undefined,
    refreshAuth: async () => undefined,
    pendingLoginKind: null,
    setApiKey: () => undefined,
    refreshSetupStatus: async () => undefined,
    onClose: () => {
      calls.closed += 1;
    },
    getErrorMessage: (error) => String(error),
    formValues: values,
    formValuesRef
  });
  const running = save();
  formValuesRef.current = { ...values, displayName: 'New' };
  configure.resolve({ requiresLoginTest: false, requiresOidcTest: false });
  await running;

  assert.equal(calls.closed, 0, 'the page must not close over an unsaved edit');
  assert.equal(calls.tests, 0);
  assert.deepEqual(calls.errors.at(-1), 'accessSetup.changedWhileSaving');
});

const guestConfiguration = 'src/components/features/user/GuestConfiguration.tsx';

/**
 * One Steam guest prefill row: the shipped draft edit, the read and the per-service save Save sends,
 * bound to one row's state. Every request waits until the test answers it.
 */
const guestHarness = () => {
  const service = {
    id: 'steam',
    shortName: 'Steam',
    supportsMaxThreads: true,
    guestConfigPath: '/api/steam/guest'
  };
  const requests = [];
  const state = {
    configs: slot({ steam: { enabledByDefault: false, durationHours: 2, maxThreadCount: 4 } }),
    draft: slot({}),
    loading: slot({ steam: false }),
    errors: []
  };
  const prefillChangedRef = { current: new Set() };
  const fetch = (path, options) => {
    const answer = deferred();
    requests.push({ path, method: options?.method ?? 'GET', body: options?.body, answer });
    return answer.promise;
  };
  const toGuestPrefillConfig = (_service, response) => ({
    enabledByDefault: response.enabledByDefault,
    durationHours: response.durationHours,
    maxThreadCount: response.maxThreadCount ?? null
  });
  // Rebound per call, so each sees the row values the latest render would hold.
  const edit = (change) =>
    bindLifted(liftConstArrow(guestConfiguration, 'editPrefill'), {
      prefillConfigs: state.configs.value,
      setDraftPrefill: state.draft.set
    })(service, change);
  const save = (change) =>
    bindLifted(liftConstArrow(guestConfiguration, 'updatePrefillConfig'), {
      prefillConfigs: state.configs.value,
      fetch,
      ApiService: { getJsonFetchOptions: (body, options) => ({ body, ...options }) },
      assertOk: async () => undefined,
      prefillChangedRef,
      setPrefillConfigs: state.configs.set,
      toGuestPrefillConfig
    })(service, change);
  const read = bindLifted(
    liftHookCallback(
      guestConfiguration,
      'useCallback',
      'service.guestConfigPath, ApiService.getFetchOptions()'
    ),
    {
      setLoadingPrefillConfigs: state.loading.set,
      fetch,
      ApiService: { getFetchOptions: () => ({}) },
      assertOk: async () => undefined,
      prefillConfigRequestRef: { current: 1 },
      prefillChangedRef,
      setPrefillConfigs: state.configs.set,
      toGuestPrefillConfig,
      setDraftPrefill: state.draft.set,
      setPrefillConfigError: (message) => state.errors.push(message),
      getErrorMessage: (error) => error.message
    }
  );
  /** Answers the oldest open request of `method` with `body`, or fails it. */
  const answer = async (method, body) => {
    const request = requests.find((entry) => entry.method === method && !entry.done);
    request.done = true;
    if (body === undefined) request.answer.reject(new Error('read failed'));
    else request.answer.resolve({ json: async () => body });
    await settle();
  };
  return { service, requests, state, edit, save, read, answer };
};

test('guest prefill: a change sends nothing until Save, then goes out on top of the read values', async () => {
  const guest = guestHarness();
  const reading = guest.read(guest.service, 1);
  guest.edit({ maxThreadCount: 8 });
  assert.deepEqual(guest.state.draft.value, { steam: { maxThreadCount: 8 } });
  await guest.answer('GET', { enabledByDefault: true, durationHours: 3, maxThreadCount: 4 });
  await reading;
  assert.deepEqual(
    guest.requests.map((request) => request.method),
    ['GET'],
    'the change sent a request before Save'
  );

  const saving = guest.save(guest.state.draft.value.steam);
  await settle();
  assert.deepEqual(guest.requests[1].body, {
    enabledByDefault: true,
    durationHours: 3,
    maxThreadCount: 8
  });
  await guest.answer('POST', guest.requests[1].body);
  await saving;
  assert.deepEqual(guest.state.configs.value.steam, {
    enabledByDefault: true,
    durationHours: 3,
    maxThreadCount: 8
  });
});

test('guest prefill: a change set back to the saved value leaves the draft', () => {
  const guest = guestHarness();
  guest.edit({ durationHours: 3 });
  guest.edit({ durationHours: 2 });
  assert.deepEqual(guest.state.draft.value, {}, 'Save would send a row nobody changed');
});

test('guest prefill: a change made while the first read runs is dropped when the read fails', async () => {
  const guest = guestHarness();
  const reading = guest.read(guest.service, 1);
  guest.edit({ enabledByDefault: true });
  await guest.answer('GET', undefined);
  await reading;

  assert.deepEqual(guest.state.errors, ['read failed']);
  assert.equal(guest.state.loading.value.steam, false);
  assert.deepEqual(
    guest.state.draft.value,
    {},
    'Save would send the panel defaults the read never replaced'
  );
});

test('guest prefill: a read that started before a save answered keeps the saved row', async () => {
  const guest = guestHarness();
  const reading = guest.read(guest.service, 1);
  const saving = guest.save({ enabledByDefault: true });
  await settle();
  await guest.answer('POST', guest.requests[1].body);
  await saving;
  await guest.answer('GET', { enabledByDefault: false, durationHours: 2, maxThreadCount: 4 });
  await reading;
  assert.equal(guest.state.configs.value.steam.enabledByDefault, true, 'the older read won');
});

/** The stored guest defaults a read answers with. */
const storedDefaults = () => ({
  useLocalTimezone: false,
  useUtcTimezone: false,
  use24HourFormat: true,
  sharpCorners: false,
  disableTooltips: false,
  showDatasourceLabels: true,
  allowedTimeFormats: ['server-24h']
});

/**
 * The shipped guest defaults read, toggle save and push handler bound to one card's state. Every
 * request waits until the test answers it.
 */
const guestDefaultsHarness = () => {
  const requests = [];
  const loadingSteps = [];
  const state = {
    preferences: slot({ ...storedDefaults(), allowedTimeFormats: ['server-24h', 'local-24h'] }),
    loading: slot(false),
    error: slot(null)
  };
  const setLoading = (value) => {
    loadingSteps.push(value);
    state.loading.set(value);
  };
  const defaultPrefsChangedRef = { current: new Set() };
  const defaultPrefsPushesRef = { current: new Map() };
  // Lifted on first use, so the cases that only read run against a file without it.
  let applyChange;
  const applyDefaultPrefsChange = (patch) => {
    applyChange ??= bindLifted(
      liftHookCallback(guestConfiguration, 'useCallback', 'defaultPrefsChangedRef.current.add'),
      { defaultPrefsChangedRef, setDefaultGuestPreferences: state.preferences.set }
    );
    applyChange(patch);
  };
  let applyPush;
  const applyDefaultPrefsPush = (patch) => {
    applyPush ??= bindLifted(
      liftHookCallback(guestConfiguration, 'useCallback', 'defaultPrefsPushesRef.current.set'),
      { defaultPrefsPushesRef, applyDefaultPrefsChange }
    );
    applyPush(patch);
  };
  let applyReply;
  const applyDefaultPrefsReply = (patch, pushesBefore) => {
    applyReply ??= bindLifted(
      liftHookCallback(guestConfiguration, 'useCallback', 'pushesBefore.get'),
      { defaultPrefsPushesRef, applyDefaultPrefsChange }
    );
    applyReply(patch, pushesBefore);
  };
  /** What every save and push handler reads to tell a reply from a newer push. */
  const replyBindings = {
    defaultPrefsPushesRef,
    applyDefaultPrefsChange,
    applyDefaultPrefsReply,
    applyDefaultPrefsPush
  };
  const fetch = (path, options) => {
    const answer = deferred();
    requests.push({ path, method: options?.method ?? 'GET', answer });
    return answer.promise;
  };
  const assertOk = async (response) => {
    if (!response.ok) throw new Error('request failed');
  };
  const load = bindLifted(liftConstArrow(guestConfiguration, 'loadDefaultGuestPreferences'), {
    defaultPrefsRequestRef: { current: 0 },
    defaultPrefsLoadedRef: { current: false },
    defaultPrefsChangedRef,
    setLoadingDefaultPrefs: setLoading,
    fetch,
    ApiService: { getFetchOptions: () => ({}) },
    assertOk,
    setDefaultGuestPreferences: state.preferences.set,
    TIME_SETTING_VALUES: ['server-24h'],
    setDefaultPrefsError: state.error.set,
    getErrorMessage: (error) => error.message,
    console: { error: () => undefined }
  });
  const savePreference = bindLifted(
    liftConstArrow(guestConfiguration, 'handleUpdateDefaultGuestPref'),
    {
      fetch,
      ApiService: { getJsonFetchOptions: (body, options) => ({ body, ...options }) },
      assertOk,
      ...replyBindings
    }
  );
  const saveClock = bindLifted(liftConstArrow(guestConfiguration, 'updateDefaultTimeFormat'), {
    fetch,
    ApiService: { getJsonFetchOptions: (body, options) => ({ body, ...options }) },
    clockFromTimeSetting: (format) => ({ format }),
    assertOk,
    ...replyBindings
  });
  const saveAllowedFormats = bindLifted(
    liftConstArrow(guestConfiguration, 'handleAllowedFormatsChange'),
    {
      attempt: bindLifted(liftConstArrow(guestConfiguration, 'attempt'), {}),
      fetch,
      ApiService: { getJsonFetchOptions: (body, options) => ({ body, ...options }) },
      assertOk,
      getCurrentDefaultFormat: () => 'server-24h',
      updateDefaultTimeFormat: saveClock,
      ...replyBindings,
      t: (key) => key
    }
  );
  const push = bindLifted(
    liftHookCallback(guestConfiguration, 'useCallback', "data.key === 'clock'"),
    { setDefaultGuestPreferences: state.preferences.set, ...replyBindings }
  );
  const pushAllowedFormats = bindLifted(
    liftHookCallback(guestConfiguration, 'useCallback', 'allowedTimeFormats: data.formats'),
    replyBindings
  );
  /** Each guest-default control's own `disabled` expression, rendered with the current flags. */
  const disabledControls = () => {
    const sourceFile = parseSource(guestConfiguration, typescript.ScriptKind.TSX);
    return collectNodes(
      sourceFile,
      (node) =>
        typescript.isJsxAttribute(node) &&
        node.name.getText(sourceFile) === 'disabled' &&
        node.initializer.expression.getText(sourceFile).includes('loadingDefaultPrefs')
    ).map((node) =>
      bindLifted(`() => (${node.initializer.expression.getText(sourceFile)})`, {
        loadingDefaultPrefs: state.loading.value
      })()
    );
  };
  /** Answers the newest open read with `preferences`, or fails it. */
  const answerRead = async (preferences) => {
    const read = requests.findLast((request) => request.method === 'GET');
    read.answer.resolve(
      preferences ? { ok: true, json: async () => preferences } : { ok: false, status: 503 }
    );
    await settle();
  };
  /** Answers the newest open write as saved, with `body` as the reply. */
  const answerWrite = async (body = {}) => {
    const write = requests.findLast((request) => request.method === 'PATCH');
    write.answer.resolve({ ok: true, json: async () => body });
    await settle();
  };
  /** Mount: the first read starts and answers with the stored defaults. */
  const firstLoad = async () => {
    void load();
    await answerRead(storedDefaults());
    loadingSteps.length = 0;
  };
  return {
    state,
    loadingSteps,
    load,
    savePreference,
    saveClock,
    saveAllowedFormats,
    push,
    pushAllowedFormats,
    disabledControls,
    answerRead,
    answerWrite,
    firstLoad
  };
};

test('guest defaults: the controls wait for the first read only', async () => {
  const card = guestDefaultsHarness();
  void card.load();
  await settle();
  assert.deepEqual(card.disabledControls(), [true, true, true, true], 'four controls held');
  await card.answerRead(storedDefaults());
  assert.equal(card.state.loading.value, false);
  assert.deepEqual(card.disabledControls(), [false, false, false, false]);
});

test('guest defaults: a reconnect reload keeps the controls usable', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  void card.load();
  await settle();
  assert.deepEqual(card.disabledControls(), [false, false, false, false]);
  await card.answerRead(storedDefaults());
  assert.ok(!card.loadingSteps.includes(true), 'the reload never raises the loading flag');
  assert.equal(card.state.loading.value, false);
});

test('guest defaults: a reload that fails keeps the card and the loaded values', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  void card.load();
  await card.answerRead(null);
  assert.equal(card.state.error.value, null, 'no error box over the loaded card');
  assert.deepEqual(card.state.preferences.value, storedDefaults());
  assert.equal(card.state.loading.value, false);
});

test('guest defaults: a toggle saved during a reload still shows after the reload answers', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  void card.load();
  await settle();
  const saving = card.savePreference('sharpCorners', true);
  await card.answerWrite();
  await saving;
  assert.equal(card.state.preferences.value.sharpCorners, true);
  // The read left before the write; it also carries a change missed while disconnected.
  await card.answerRead({ ...storedDefaults(), disableTooltips: true });
  assert.equal(card.state.preferences.value.sharpCorners, true, 'the saved toggle stays');
  assert.equal(card.state.preferences.value.disableTooltips, true, 'the missed change arrives');
});

test('guest defaults: a push received during a reload still shows after the reload answers', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  void card.load();
  await settle();
  card.push({ key: 'showDatasourceLabels', value: false });
  await card.answerRead(storedDefaults());
  assert.equal(card.state.preferences.value.showDatasourceLabels, false, 'the pushed value stays');
});

// The server announces a save before it answers it, so a push that lands while the save is open
// carries a value at least as new as the reply's.
test('guest defaults: a push received while a toggle saves wins over the save reply', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  const saving = card.savePreference('sharpCorners', true);
  await settle();
  card.push({ key: 'sharpCorners', value: false });
  await card.answerWrite();
  await saving;
  assert.equal(card.state.preferences.value.sharpCorners, false, 'the pushed value stays');
});

test('guest defaults: a toggle save with no push shows the saved value', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  const saving = card.savePreference('sharpCorners', true);
  await card.answerWrite();
  await saving;
  assert.equal(card.state.preferences.value.sharpCorners, true);
});

const savedClock = { useUtcTimezone: false, useLocalTimezone: true, use24HourFormat: false };
const pushedClock = { useUtcTimezone: true, useLocalTimezone: false, use24HourFormat: true };
const clockOf = (preferences) => ({
  useUtcTimezone: preferences.useUtcTimezone,
  useLocalTimezone: preferences.useLocalTimezone,
  use24HourFormat: preferences.use24HourFormat
});

test('guest defaults: a push received while the clock saves wins over the save reply', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  const saving = card.saveClock('local-12h');
  await settle();
  card.push({ key: 'clock', clock: pushedClock, previousClock: savedClock });
  await card.answerWrite({ clock: savedClock });
  await saving;
  assert.deepEqual(clockOf(card.state.preferences.value), pushedClock, 'the pushed clock stays');
});

test('guest defaults: a clock save with no push shows the saved clock', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  const saving = card.saveClock('local-12h');
  await card.answerWrite({ clock: savedClock });
  await saving;
  assert.deepEqual(clockOf(card.state.preferences.value), savedClock);
});

test('guest defaults: a push received while the allowed formats save wins over the save reply', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  const saving = card.saveAllowedFormats(['server-24h', 'server-12h']);
  await settle();
  card.pushAllowedFormats({ formats: ['server-24h', 'local-24h'] });
  await card.answerWrite();
  await saving;
  assert.deepEqual(card.state.preferences.value.allowedTimeFormats, ['server-24h', 'local-24h']);
});

test('guest defaults: an allowed formats save with no push shows the saved formats', async () => {
  const card = guestDefaultsHarness();
  await card.firstLoad();
  const saving = card.saveAllowedFormats(['server-24h', 'server-12h']);
  await card.answerWrite();
  await saving;
  assert.deepEqual(card.state.preferences.value.allowedTimeFormats, ['server-24h', 'server-12h']);
});

const userTab = 'src/components/features/user/UserTab.tsx';

/** The guest session length the Users screen holds before a duration save or push. */
const loadedDuration = { durationHours: 6, source: 'ui', canEdit: true, envVarValue: 6 };

/**
 * One guest setting on the Users screen: its shipped save, the ApiService call that save waits on,
 * the state setter both write, and the text only its push handler holds.
 */
const userTabGuestSettings = [
  {
    save: 'handleUpdateDuration',
    call: 'setGuestSessionDuration',
    setter: 'setGuestDuration',
    pushText: 'data.durationHours',
    initial: loadedDuration,
    input: 12,
    reply: { ...loadedDuration, durationHours: 12 },
    saved: { ...loadedDuration, durationHours: 12 },
    push: { durationHours: 24 },
    pushed: { ...loadedDuration, durationHours: 24 }
  },
  {
    save: 'handleToggleGuestLock',
    call: 'setGuestConfigLock',
    setter: 'setGuestModeLocked',
    pushText: 'data.isLocked',
    input: true,
    reply: undefined,
    saved: true,
    push: { isLocked: false },
    pushed: false
  },
  {
    save: 'handleUpdateGuestTheme',
    call: 'setGuestThemePreference',
    setter: 'setDefaultGuestTheme',
    pushText: 'data.newThemeId',
    input: 'light-default',
    reply: undefined,
    saved: 'light-default',
    push: { newThemeId: 'dark-default' },
    pushed: 'dark-default'
  },
  {
    save: 'handleUpdateGuestRefreshRate',
    call: 'setDefaultGuestRefreshRate',
    setter: 'setDefaultGuestRefreshRate',
    pushText: 'data.refreshRate',
    input: 'LIVE',
    reply: undefined,
    saved: 'LIVE',
    push: { refreshRate: 'SLOW' },
    pushed: 'SLOW'
  },
  {
    save: 'handleUpdateGuestRefreshRateLock',
    call: 'setGuestRefreshRateLock',
    setter: 'setGuestRefreshRateLocked',
    pushText: 'data.locked',
    input: false,
    reply: undefined,
    saved: false,
    push: { locked: true },
    pushed: true
  }
];

/** A setting's shipped save and push handler bound to one shared push count and a recording setter. */
const userTabGuestHarness = (setting) => {
  const call = deferred();
  const values = [];
  const guestDefaultPushesRef = {
    current: { duration: 0, guestLock: 0, theme: 0, refreshRate: 0, refreshRateLock: 0 }
  };
  const shown = slot(setting.initial);
  const record = (next) => {
    shown.set(next);
    values.push(shown.value);
  };
  const save = bindLifted(liftConstArrow(userTab, setting.save), {
    ApiService: { [setting.call]: () => call.promise },
    [setting.setter]: record,
    guestDefaultPushesRef
  });
  const push = bindLifted(liftHookCallback(userTab, 'useCallback', setting.pushText), {
    [setting.setter]: record,
    guestDefaultPushesRef,
    loadGuestDefaults: async () => undefined
  });
  return { call, values, save, push };
};

for (const setting of userTabGuestSettings) {
  test(`users screen: a push received while ${setting.save} saves wins over the save reply`, async () => {
    const screen = userTabGuestHarness(setting);
    const saving = screen.save(setting.input);
    await settle();
    screen.push(setting.push);
    screen.call.resolve(setting.reply);
    await saving;
    assert.deepEqual(screen.values.at(-1), setting.pushed, 'the pushed value stays');
  });

  test(`users screen: ${setting.save} with no push shows the saved value`, async () => {
    const screen = userTabGuestHarness(setting);
    const saving = screen.save(setting.input);
    await settle();
    screen.call.resolve(setting.reply);
    await saving;
    assert.deepEqual(screen.values.at(-1), setting.saved);
  });

  test(`users screen: ${setting.save} rejects when the server refuses, so Save can name it`, async () => {
    const screen = userTabGuestHarness(setting);
    const saving = screen.save(setting.input);
    screen.call.reject(new Error('server said no'));
    await assert.rejects(saving, /server said no/);
    assert.deepEqual(screen.values, [], 'a refused value must not show as saved');
  });
}

test('users screen: a push received while the guest defaults load is open wins over the load', async () => {
  const answers = deferred();
  const shown = { duration: loadedDuration };
  const guestDefaultPushesRef = {
    current: { duration: 0, guestLock: 0, theme: 0, refreshRate: 0, refreshRateLock: 0 }
  };
  const into = (name) => (value) => {
    shown[name] = typeof value === 'function' ? value(shown[name]) : value;
  };
  const setters = {
    setGuestDuration: into('duration'),
    setGuestModeLocked: into('guestLock'),
    setDefaultGuestTheme: into('theme'),
    setDefaultGuestRefreshRate: into('refreshRate'),
    setGuestRefreshRateLocked: into('refreshRateLock')
  };
  const load = bindLifted(liftHookCallback(userTab, 'useCallback', 'getGuestConfig'), {
    ...setters,
    guestDefaultsRequestRef: { current: 0 },
    guestDefaultPushesRef,
    setGuestDefaultsError: () => undefined,
    setGuestDefaultsLoaded: () => undefined,
    setLoadingGuestDefaults: () => undefined,
    setAvailableThemes: () => undefined,
    getErrorMessage: (error) => String(error),
    themeService: { loadThemes: async () => ({ themes: [], loadError: null }) },
    ApiService: {
      getGuestConfig: () => answers.promise.then(() => ({ isLocked: true })),
      getGuestSessionDuration: () => answers.promise.then(() => loadedDuration),
      getGuestThemePreference: () => answers.promise.then(() => ({ themeId: 'light-default' })),
      getDefaultGuestRefreshRate: () =>
        answers.promise.then(() => ({ refreshRate: 'STANDARD', locked: true }))
    }
  });
  const push = (setting) =>
    bindLifted(liftHookCallback(userTab, 'useCallback', setting.pushText), {
      [setting.setter]: setters[setting.setter],
      guestDefaultPushesRef,
      loadGuestDefaults: async () => undefined
    });
  const [duration, , theme] = userTabGuestSettings;

  const loading = load();
  await settle();
  // Another admin saves two settings while the reads are out; the server pushes before it answers.
  push(duration)(duration.push);
  push(theme)(theme.push);
  answers.resolve();
  await loading;

  assert.deepEqual(shown.duration, duration.pushed, 'the pushed duration stays');
  assert.equal(shown.theme, theme.pushed, 'the pushed theme stays');
  // Settings with no push take the values the load read.
  assert.equal(shown.guestLock, true);
  assert.equal(shown.refreshRate, 'STANDARD');
  assert.equal(shown.refreshRateLock, true);
});

const sessionPreferences = 'src/components/features/user/SessionPreferences.tsx';

/**
 * One session's preferences pane: the shipped `savePreference` and `reloadPrefs` bound to the
 * pane's refs and a server that holds one preferences object. Every request stays open until the
 * test answers it, oldest first.
 */
const preferencesPaneHarness = () => {
  const stored = declaredValue('src/types/userPreferences.ts', 'DEFAULT_PREFERENCES');
  const server = { current: { ...stored } };
  const requests = [];
  const errors = [];
  const shown = slot({ ...stored });
  const request = (method, body) => {
    const answer = deferred();
    requests.push({ method, body, answer, open: true });
    return answer.promise;
  };
  let reload;
  const bindings = {
    session: { id: 'session-1', isCurrentSession: false },
    writeChainRef: { current: Promise.resolve() },
    confirmedRef: { current: { ...stored } },
    pendingRef: { current: new Map() },
    pendingTotalRef: { current: 0 },
    clickCountRef: { current: 0 },
    reloadRequestRef: { current: 0 },
    reloadOwedRef: { current: false },
    setPrefs: shown.set,
    setPrefsLoaded: () => undefined,
    setLoadError: () => undefined,
    setPrefsRetrying: () => undefined,
    setSavingField: () => undefined,
    getErrorMessage: (error) => error.message,
    ApiService: {
      getSessionPreferences: () => request('GET'),
      saveSessionPreferences: (_id, body) => request('PUT', body)
    },
    notifyError: (_message, error) => errors.push(error),
    t: (key) => key,
    reloadPrefs: () => reload()
  };
  reload = bindLifted(liftConstArrow(sessionPreferences, 'reloadPrefs'), bindings);
  // Each row's write runs in the shipped `writePreference`, the step the pane's Save runs per row;
  // its failure is reported the way Save reports it.
  bindings.writePreference = bindLifted(
    liftConstArrow(sessionPreferences, 'writePreference'),
    bindings
  );
  const save = (key, value) =>
    void bindings
      .writePreference(key, value)
      .catch((error) => bindings.notifyError('activeSessions.errors.savePreferences', error));
  /** Takes the oldest open request off the wire. */
  const nextOpen = () => {
    const next = requests.find((entry) => entry.open);
    next.open = false;
    return next;
  };
  /** Answers the oldest open request: a read with what the server holds, a write by storing it. */
  const respond = async () => {
    const next = nextOpen();
    if (next.method === 'PUT') server.current = next.body;
    next.answer.resolve(next.method === 'GET' ? { ...server.current } : undefined);
    await settle();
  };
  /** Fails the oldest open request. */
  const fail = async () => {
    nextOpen().answer.reject(new Error('request failed'));
    await settle();
  };
  const methods = () => requests.map((entry) => entry.method);
  return { server, errors, shown, save, reload, respond, fail, methods };
};

test('session preferences: a second row change waits for the first write to finish, and both are stored', async () => {
  const pane = preferencesPaneHarness();
  pane.save('sharpCorners', true);
  pane.save('disableTooltips', true);
  await settle();
  assert.deepEqual(pane.methods(), ['GET'], 'one write runs at a time');
  await pane.respond();
  assert.deepEqual(pane.methods(), ['GET', 'PUT']);
  await pane.respond();
  assert.deepEqual(pane.methods(), ['GET', 'PUT', 'GET'], 'the second read follows the first PUT');
  await pane.respond();
  await pane.respond();
  assert.equal(pane.server.current.sharpCorners, true);
  assert.equal(pane.server.current.disableTooltips, true);
  assert.equal(pane.shown.value.sharpCorners, true);
  assert.equal(pane.shown.value.disableTooltips, true);
});

test('session preferences: a failed write leaves the write queued behind it to run', async () => {
  const pane = preferencesPaneHarness();
  pane.save('sharpCorners', true);
  pane.save('disableTooltips', true);
  await settle();
  await pane.respond();
  await pane.fail();
  assert.deepEqual(pane.methods(), ['GET', 'PUT', 'GET'], 'the queued write still starts');
  await pane.respond();
  await pane.respond();
  assert.equal(pane.errors.length, 1);
  assert.equal(pane.server.current.sharpCorners, false);
  assert.equal(pane.server.current.disableTooltips, true);
  assert.equal(pane.shown.value.sharpCorners, false, 'the failed row shows what the server holds');
  assert.equal(pane.shown.value.disableTooltips, true);
});

/** Theme picked twice while the tooltips row saves; the first theme write fails. */
const changeThemeTwiceWhileAnotherRowSaves = async (pane) => {
  pane.save('disableTooltips', true);
  pane.save('selectedTheme', 'light-a');
  pane.save('selectedTheme', 'light-b');
  await settle();
  await pane.respond();
  await pane.respond();
  await pane.respond();
  await pane.fail();
};

test('session preferences: a row changed twice whose first write fails ends on the second value', async () => {
  const pane = preferencesPaneHarness();
  await changeThemeTwiceWhileAnotherRowSaves(pane);
  assert.equal(pane.shown.value.selectedTheme, 'light-b', 'the queued value stays on screen');
  await pane.respond();
  await pane.respond();
  assert.equal(pane.server.current.selectedTheme, 'light-b');
  assert.equal(pane.shown.value.selectedTheme, 'light-b');
});

test('session preferences: a row changed twice whose writes both fail ends on the last stored value', async () => {
  const pane = preferencesPaneHarness();
  await changeThemeTwiceWhileAnotherRowSaves(pane);
  await pane.respond();
  await pane.fail();
  assert.equal(pane.server.current.selectedTheme, null);
  assert.equal(
    pane.shown.value.selectedTheme,
    null,
    'the row never shows a value the server lacks'
  );
  assert.equal(pane.shown.value.disableTooltips, true);
});

test('session preferences: a reload read before a click never flips the clicked row back', async () => {
  const pane = preferencesPaneHarness();
  void pane.reload();
  await settle();
  pane.save('sharpCorners', true);
  await settle();
  assert.deepEqual(pane.methods(), ['GET', 'GET']);
  // The reload's read was taken before the click, so it still holds the old value.
  await pane.respond();
  assert.equal(pane.shown.value.sharpCorners, true, 'the switch keeps the clicked value');
  await pane.respond();
  await pane.respond();
  assert.equal(pane.shown.value.sharpCorners, true);
  assert.deepEqual(pane.methods(), ['GET', 'GET', 'PUT', 'GET'], 'one more read after the write');
  await pane.respond();
  assert.equal(pane.shown.value.sharpCorners, true);
});

/**
 * The Users screen's pick check, run once against the lists as one render sees them. The pick the
 * screen holds is `pick.value`; the stored copy is in `localStorage`, as on the page.
 */
const pickCheckHarness = async (stored) => {
  globalThis.localStorage = new MemoryStorage();
  const { storage } = await import(await compileToUrl('../src/utils/storage.ts'));
  const PICK_STORAGE_KEY = declaredValue(userTab, 'PICK_STORAGE_KEY');
  storage.setJSON(PICK_STORAGE_KEY, stored);
  const pick = slot(stored);
  const sourceFile = parseSource(userTab);
  /** A `const` of the screen whose initializer reads only `bindings`, evaluated. */
  const derived = (name, bindings) =>
    bindLifted(
      `() => (${findSoleNode(
        sourceFile,
        `${name} declaration`,
        (node) => typescript.isVariableDeclaration(node) && node.name.getText(sourceFile) === name
      ).initializer.getText(sourceFile)})`,
      bindings
    )();
  /**
   * @param {{ loaded: boolean, loading: boolean, loadError: string | null }} sessionsState
   * @param {{ loaded: boolean, loading: boolean }} accountsState
   * @param {{ id: string, accountId: string | null }[]} sessions
   * @param {{ id: string }[]} accounts
   */
  const check = (sessionsState, accountsState, sessions, accounts) => {
    const activeSessionsOf = bindLifted(liftConstArrow(userTab, 'activeSessionsOf'), { sessions });
    bindLifted(liftHookCallback(userTab, 'useEffect', 'fallBack'), {
      pick: pick.value,
      setPick: pick.set,
      storage,
      PICK_STORAGE_KEY,
      setPhoneView: () => undefined,
      signInVisible: true,
      sessionsReady: derived('sessionsReady', { sessionsState }),
      accountsReady: derived('accountsReady', { accountsState }),
      sessions,
      historySessions: [],
      accounts,
      activeSessionsOf,
      sessionsState
    })();
    // What the detail pane draws for the pick: the session it shows, or the list it waits on.
    return bindLifted(
      liftConstArrow(userTab, 'detailContent'),
      {
        shownPick: pick.value,
        signInVisible: true,
        findSession: bindLifted(liftConstArrow(userTab, 'findSession'), {
          sessions,
          historySessions: []
        }),
        sessionPane: (session) => `session ${session.id}`,
        waitingPane: (list) => `waiting for ${list}`,
        accounts,
        accountActions: {},
        AccountDetail: 'AccountDetail',
        React: { createElement: (_type, props) => `account ${props.account.id}` }
      },
      { jsx: typescript.JsxEmit.React }
    )();
  };
  const storedPick = () => storage.getJSON(PICK_STORAGE_KEY);
  return { pick, check, storedPick };
};

const sessionsLoading = { loaded: false, loading: true, loadError: null };
const sessionsFailed = { loaded: false, loading: false, loadError: 'sessions unavailable' };
const sessionsLoaded = { loaded: true, loading: false, loadError: null };
const accountsLoaded = { loaded: true, loading: false };

test('users screen: a stored session pick is kept while the session list loads or fails', async () => {
  const stored = { kind: 'session', id: 'session-s' };
  const screen = await pickCheckHarness(stored);
  screen.check(sessionsLoading, accountsLoaded, [], []);
  assert.deepEqual(screen.pick.value, stored);
  assert.deepEqual(screen.storedPick(), stored);
  screen.check(sessionsFailed, accountsLoaded, [], []);
  assert.deepEqual(screen.pick.value, stored);
  assert.deepEqual(screen.storedPick(), stored);
  // Once a load answers without the session, the pick falls back.
  screen.check(sessionsLoaded, accountsLoaded, [], []);
  assert.equal(screen.pick.value, null);
  assert.equal(screen.storedPick(), null);
});

test('users screen: an account pick keeps its session while sessions load or fail, then opens it', async () => {
  const stored = { kind: 'account', id: 'account-a', sessionId: 'session-s' };
  const accounts = [{ id: 'account-a', username: 'alice' }];
  const screen = await pickCheckHarness(stored);
  assert.equal(screen.check(sessionsLoading, accountsLoaded, [], accounts), 'waiting for sessions');
  assert.deepEqual(screen.storedPick(), stored);
  assert.equal(screen.check(sessionsFailed, accountsLoaded, [], accounts), 'waiting for sessions');
  assert.deepEqual(screen.storedPick(), stored);
  const sessions = [{ id: 'session-s', accountId: 'account-a' }];
  assert.equal(
    screen.check(sessionsLoaded, accountsLoaded, sessions, accounts),
    'session session-s'
  );
  assert.deepEqual(screen.pick.value, stored);
  assert.deepEqual(screen.storedPick(), stored);
});

test('scheduled prefill schedule: an edit made while Save runs keeps the dialog open and dirty', async () => {
  const update = deferred();
  const calls = { closed: 0, shown: [] };
  const sent = { id: 's1', name: 'Nightly', enabled: true };
  const configRef = { current: sent };
  const dirty = { current: true };
  const commitSave = bindLifted(
    liftConstArrow(
      'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
      'commitSave'
    ),
    {
      target: { serviceKey: 'steam', opening: 1, create: false },
      config: sent,
      saving: false,
      missing: false,
      writes: { current: new Map() },
      setSaving: () => undefined,
      setError: () => undefined,
      ApiService: { updateScheduledPrefillSchedule: () => update.promise },
      getPersistentServiceId: () => 'steam',
      t: (key) => key,
      baseKey: 'base',
      confirmed: { current: { onSaved: () => undefined } },
      current: { current: { opening: 1 } },
      baseline: { current: JSON.stringify(sent) },
      dirty,
      setConfig: (value) => calls.shown.push(value),
      onClose: () => {
        calls.closed += 1;
      },
      getErrorMessage: (error) => String(error),
      configRef,
      createdOpening: { current: null }
    }
  );
  const running = commitSave();
  configRef.current = { ...sent, name: 'Nightly run' };
  update.resolve({ steam: { schedules: [sent] } });
  await running;

  assert.equal(calls.closed, 0, 'the dialog must not close over an unsaved edit');
  assert.deepEqual(calls.shown, [], 'the edit stays on screen');
  assert.equal(dirty.current, true);
});

test('scheduled prefill schedule: the Save after an edit during a create updates, not creates', async () => {
  const calls = { created: 0, updated: 0 };
  const sent = { id: 's2', name: 'New', enabled: true };
  const configRef = { current: sent };
  const createdOpening = { current: null };
  const bind = (config) =>
    bindLifted(
      liftConstArrow(
        'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
        'commitSave'
      ),
      {
        target: { serviceKey: 'steam', opening: 4, create: true },
        config,
        saving: false,
        missing: false,
        writes: { current: new Map() },
        setSaving: () => undefined,
        setError: () => undefined,
        ApiService: {
          createScheduledPrefillSchedule: async () => {
            calls.created += 1;
            configRef.current = { ...sent, name: 'New run' };
            return { steam: { schedules: [sent] } };
          },
          updateScheduledPrefillSchedule: async (_service, record) => {
            calls.updated += 1;
            return { steam: { schedules: [record] } };
          }
        },
        getPersistentServiceId: () => 'steam',
        t: (key) => key,
        baseKey: 'base',
        confirmed: { current: { onSaved: () => undefined } },
        current: { current: { opening: 4 } },
        baseline: { current: null },
        dirty: { current: true },
        setConfig: () => undefined,
        onClose: () => undefined,
        getErrorMessage: (error) => String(error),
        configRef,
        createdOpening
      }
    );
  await bind(sent)();
  await bind(configRef.current)();

  assert.deepEqual(calls, { created: 1, updated: 1 });
});

test('setup steps: every field is read-only while its own submit runs', () => {
  const steps = [
    'src/components/initialization/steps/AdminAccountStep.tsx',
    'src/components/initialization/steps/DatabaseSetupStep.tsx',
    'src/components/initialization/steps/ExternalDatabaseSetupStep.tsx'
  ];
  for (const path of steps) {
    const sourceFile = parseSource(path, typescript.ScriptKind.TSX);
    const fields = collectNodes(
      sourceFile,
      (node) =>
        (typescript.isJsxSelfClosingElement(node) || typescript.isJsxOpeningElement(node)) &&
        ['input', 'PasswordField', 'PostgresConnectionFields'].includes(
          node.tagName.getText(sourceFile)
        )
    );
    assert.ok(fields.length > 0, path);
    for (const field of fields) {
      const readOnly = field.attributes.properties.find(
        (attribute) =>
          typescript.isJsxAttribute(attribute) && attribute.name.getText(sourceFile) === 'readOnly'
      );
      assert.equal(
        readOnly?.initializer?.expression?.getText(sourceFile),
        'isSubmitting',
        `${path}: ${field.tagName.getText(sourceFile)} at line ${
          sourceFile.getLineAndCharacterOfPosition(field.getStart()).line + 1
        }`
      );
    }
  }
  for (const path of [
    'src/components/ui/PasswordField.tsx',
    'src/components/ui/PostgresConnectionFields.tsx'
  ]) {
    const sourceFile = parseSource(path, typescript.ScriptKind.TSX);
    const passed = collectNodes(
      sourceFile,
      (node) =>
        typescript.isJsxAttribute(node) &&
        node.name.getText(sourceFile) === 'readOnly' &&
        node.initializer?.expression?.getText(sourceFile) === 'readOnly'
    );
    assert.ok(passed.length > 0, `${path} passes readOnly to its inputs`);
  }
});

const schedulesSection = 'src/components/features/management/schedules/SchedulesSection.tsx';

for (const [label, contains, request, setOpen, setError, failedKey] of [
  [
    'Reset defaults',
    'ApiService.resetSchedules()',
    'resetSchedules',
    'setResetConfirmOpen',
    'setResetError',
    'management.schedules.resetFailed'
  ],
  [
    'Run all',
    'ApiService.runAllSchedules()',
    'runAllSchedules',
    'setRunAllConfirmOpen',
    'setRunAllError',
    'management.schedules.runAllFailed'
  ]
]) {
  test(`schedules: a failed ${label} keeps its dialog open with the reason inside`, async () => {
    const opened = [];
    const errors = [];
    const handler = bindLifted(liftHookCallback(schedulesSection, 'useCallback', contains), {
      ApiService: {
        [request]: async () => {
          throw new Error('server said no');
        }
      },
      fetchSchedules: async () => undefined,
      flashAll: () => undefined,
      addNotification: () => undefined,
      notifySuccess: () => undefined,
      notifyError: (message) => errors.push(message),
      t: (key) => key,
      getErrorMessage: (error) => error.message,
      setResetting: () => undefined,
      setRunningAll: () => undefined,
      [setOpen]: (value) => opened.push(value),
      [setError]: (value) => errors.push(value)
    });
    await handler();

    assert.deepEqual(opened, [], 'the dialog stays open');
    assert.ok(errors.includes('server said no'), 'the reason is shown in the dialog');
    assert.ok(errors.includes(failedKey));
  });
}

const depotStep = 'src/components/initialization/steps/DepotInitStep.tsx';

const depotHarness = (status) => {
  const post = deferred();
  const state = { error: null, downloadStatus: null, initializing: false, selectedMethod: null };
  const calls = { finished: 0 };
  const bindings = {
    t: (key) => key,
    setInitializing: (value) => {
      state.initializing = value;
    },
    setSelectedMethod: (value) => {
      state.selectedMethod = value;
      bindings.selectedMethodRef.current = value;
    },
    setError: (value) => {
      state.error = typeof value === 'function' ? value(state.error) : value;
    },
    setDownloadStatus: (value) => {
      state.downloadStatus = value;
    },
    selectedMethodRef: { current: null },
    importRequestRef: { current: false },
    ApiService: {
      downloadPrecreatedDepotData: () => post.promise,
      getPicsStatus: async () => status
    },
    isAbortError: () => false,
    describeError: (error) => error.message,
    getErrorMessage: (error) => String(error),
    finishImport: () => {
      calls.finished += 1;
    },
    console
  };
  const handleDownload = bindLifted(liftConstArrow(depotStep, 'handleDownload'), bindings);
  const checkActiveOperation = bindLifted(
    liftHookCallback(depotStep, 'useCallback', 'getPicsStatus'),
    bindings
  );
  return { post, state, calls, handleDownload, checkActiveOperation };
};

test('depot step: the import request answering success finishes the step', async () => {
  const step = depotHarness({ steamKit2: { isRebuildRunning: false }, database: {} });
  const running = step.handleDownload();
  step.post.resolve({});
  await running;

  assert.equal(step.calls.finished, 1);
});

test('depot step: a reconnect while the import request is open reports nothing, a failure stays failed', async () => {
  const step = depotHarness({
    steamKit2: { isRebuildRunning: false },
    database: { totalMappings: 42 }
  });
  const running = step.handleDownload();
  await step.checkActiveOperation();
  assert.equal(
    step.calls.finished,
    0,
    'stored mappings from an earlier import are not this result'
  );
  assert.equal(step.state.error, null);

  step.post.reject(new Error('GitHub import failed'));
  await running;
  assert.equal(step.calls.finished, 0);
  assert.equal(step.state.error, 'GitHub import failed');
});

test('depot step: a reconnect before the server claims the import shows no failure', async () => {
  const step = depotHarness({
    steamKit2: { isRebuildRunning: false },
    database: { totalMappings: 0 }
  });
  const running = step.handleDownload();
  await step.checkActiveOperation();
  assert.equal(step.state.error, null);
  assert.equal(step.state.selectedMethod, 'cloud');

  step.post.resolve({});
  await running;
  assert.equal(step.calls.finished, 1);
});

test('database recovery form: a key or password edited during the request is kept', async () => {
  const response = deferred();
  const apiKey = slot('key-1');
  const password = slot('password-1');
  const handleSubmit = bindLifted(
    liftHookCallback('src/contexts/ConfigContext.tsx', 'useCallback', 'setup/credentials'),
    {
      canSubmit: true,
      isSaving: false,
      setIsSaving: () => undefined,
      setFailureMessage: () => undefined,
      setSavedMessage: () => undefined,
      fetch: () => response.promise,
      API_BASE: '/api',
      ApiService: {
        getJsonFetchOptions: () => ({}),
        handleResponse: async (answer) => answer.json()
      },
      username: 'postgres',
      password: password.value,
      apiKey: apiKey.value,
      setApiKey: apiKey.set,
      setPassword: password.set,
      t: (key) => key,
      onSaved: () => undefined,
      getErrorMessage: (error) => String(error)
    }
  );
  const running = handleSubmit();
  password.set('password-2');
  response.resolve({ ok: true, json: async () => ({ success: true, message: 'saved' }) });
  await running;

  assert.equal(password.value, 'password-2', 'the correction stays in the box');
  assert.equal(apiKey.value, '', 'the unchanged key is cleared as before');
});
