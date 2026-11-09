import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import {
  bindLifted,
  collectNodes,
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
    liftConstArrow('src/components/features/user/UserAccounts.tsx', 'setDisabled'),
    {
      setDisabledInProgressRef: { current: new Set() },
      setBusyAccountId: () => undefined,
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
  const accountsFile = 'src/components/features/user/UserAccounts.tsx';
  const requests = [];
  const answers = [];
  const busy = slot(null);
  const bindings = {
    setDisabledInProgressRef: { current: new Set() },
    deleteInProgressRef: { current: false },
    setBusyAccountId: busy.set,
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
    setConfirmation: () => undefined,
    setDeleteError: () => undefined,
    getErrorMessage: (error) => String(error),
    notifyError: () => undefined,
    t: (key) => key
  };
  const setDisabled = bindLifted(liftConstArrow(accountsFile, 'setDisabled'), bindings);
  const runConfirmation = bindLifted(liftConstArrow(accountsFile, 'runConfirmation'), bindings);

  const disabling = setDisabled({ id: 'a' }, true);
  const deleting = runConfirmation({ account: { id: 'b' } });
  const repeat = runConfirmation({ account: { id: 'b' } });
  answers[0].resolve({});
  await disabling;

  assert.equal(busy.value, 'b', 'the delete dialog must stay busy while its request runs');
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

const guestHarness = ({ loading = false } = {}) => {
  const service = {
    id: 'steam',
    shortName: 'Steam',
    supportsMaxThreads: true,
    guestConfigPath: '/api/steam/guest'
  };
  const bodies = [];
  const answers = [];
  const state = {
    configs: slot({ steam: { enabledByDefault: false, durationHours: 2, maxThreadCount: 4 } }),
    loading: slot({ steam: loading }),
    updating: slot({ steam: null }),
    queued: slot({ steam: null }),
    sending: slot({ steam: null })
  };
  const updateSource = liftHookCallback(
    guestConfiguration,
    'useCallback',
    'setSendingPrefillEdits'
  );
  const bindings = () => {
    const scope = {
      authMode: 'authenticated',
      loadingPrefillConfigs: state.loading.value,
      updatingPrefillConfigs: state.updating.value,
      queuedPrefillEdits: state.queued.value,
      prefillConfigs: state.configs.value,
      setUpdatingPrefillConfigs: state.updating.set,
      setQueuedPrefillEdits: state.queued.set,
      sendingPrefillEdits: state.sending.value,
      setSendingPrefillEdits: state.sending.set,
      setPrefillConfigs: state.configs.set,
      fetch: (_path, options) => {
        bodies.push(options.body);
        const answer = deferred();
        answers.push(answer);
        return answer.promise;
      },
      ApiService: { getJsonFetchOptions: (body) => ({ body }) },
      assertOk: async () => undefined,
      toGuestPrefillConfig: (_service, response) => ({
        enabledByDefault: response.enabledByDefault,
        durationHours: response.durationHours,
        maxThreadCount: response.maxThreadCount ?? null
      }),
      showToast: () => undefined,
      notifyError: () => undefined,
      t: (key) => key,
      PREFILL_SERVICES: [service]
    };
    scope.updatePrefillConfig = bindLifted(updateSource, scope);
    return scope;
  };
  const answer = (index) => {
    const body = bodies[index];
    answers[index].resolve({ json: async () => body });
  };
  return { service, bodies, answer, state, bindings };
};

test('guest prefill: a second change while a save runs is held, then sent with the first', async () => {
  const guest = guestHarness();
  const first = guest.bindings().updatePrefillConfig(guest.service, 'duration', {
    durationHours: 6
  });
  // The duration POST is on the wire; the thread limit changes in the next render.
  await guest.bindings().updatePrefillConfig(guest.service, 'maxThreads', { maxThreadCount: 8 });
  assert.equal(guest.bodies.length, 1, 'no second request while one runs');
  assert.deepEqual(guest.state.queued.value.steam?.edits, { maxThreadCount: 8 });

  guest.answer(0);
  await first;
  const effect = bindLifted(
    liftHookCallback(guestConfiguration, 'useEffect', 'queuedPrefillEdits[service.id]'),
    guest.bindings()
  );
  effect();
  await settle();
  assert.equal(guest.bodies.length, 2);
  assert.deepEqual(guest.bodies[1], {
    enabledByDefault: false,
    durationHours: 6,
    maxThreadCount: 8
  });
});

test('guest prefill: a change while the first read runs is held and sent when it ends', async () => {
  const guest = guestHarness({ loading: true });
  await guest.bindings().updatePrefillConfig(guest.service, 'enabled', { enabledByDefault: true });
  assert.equal(guest.bodies.length, 0, 'no request while the read is open');
  assert.deepEqual(guest.state.queued.value.steam?.edits, { enabledByDefault: true });

  guest.state.loading.set({ steam: false });
  const effect = bindLifted(
    liftHookCallback(guestConfiguration, 'useEffect', 'queuedPrefillEdits[service.id]'),
    guest.bindings()
  );
  effect();
  await settle();
  assert.deepEqual(guest.bodies, [{ enabledByDefault: true, durationHours: 2, maxThreadCount: 4 }]);
});

test('guest prefill: a change held while the first read runs is dropped when the read fails', async () => {
  const guest = guestHarness({ loading: true });
  await guest.bindings().updatePrefillConfig(guest.service, 'enabled', { enabledByDefault: true });
  assert.deepEqual(guest.state.queued.value.steam?.edits, { enabledByDefault: true });

  const errors = [];
  const loadPrefillConfig = bindLifted(
    liftHookCallback(
      guestConfiguration,
      'useCallback',
      'service.guestConfigPath, ApiService.getFetchOptions()'
    ),
    {
      setLoadingPrefillConfigs: guest.state.loading.set,
      fetch: () => Promise.reject(new Error('read failed')),
      ApiService: { getFetchOptions: () => ({}) },
      assertOk: async () => undefined,
      prefillConfigRequestRef: { current: 1 },
      setPrefillConfigs: guest.state.configs.set,
      toGuestPrefillConfig: () => undefined,
      setPrefillConfigError: (message) => errors.push(message),
      getErrorMessage: (error) => error.message,
      setQueuedPrefillEdits: guest.state.queued.set
    }
  );
  await loadPrefillConfig(guest.service, 1);
  const effect = bindLifted(
    liftHookCallback(guestConfiguration, 'useEffect', 'queuedPrefillEdits[service.id]'),
    guest.bindings()
  );
  effect();
  await settle();

  assert.deepEqual(errors, ['read failed']);
  assert.equal(guest.state.loading.value.steam, false);
  assert.equal(guest.state.queued.value.steam, null);
  assert.deepEqual(guest.bodies, [], 'no save over the panel defaults the read never replaced');
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
      state.error = value;
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
