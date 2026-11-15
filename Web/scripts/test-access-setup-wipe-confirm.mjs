import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow } from './transpile-module.mjs';

/**
 * Turning sign-in off deletes every account, so the server refuses the first save with a request
 * to confirm that names the count. These cases run the access settings' shipped Save against that
 * answer: the refusal opens the confirm instead of an error, Confirm sends the confirmation, and a
 * Confirm that fails keeps its message inside the dialog.
 */

/** The stage key the server answers an unconfirmed switch with. */
const CONFIRM_KEY = 'accessSetup.wipeConfirm.required';

/** Stands in for the client's API error: `save` reads only its `body`. */
class ApiError extends Error {
  constructor(body) {
    super(body.error);
    this.body = body;
  }
}

/** Save on the access settings with sign-in being turned off, its request answered by `answer`. */
const saveHarness = (answer) => {
  const calls = { requests: [], errors: [], wipeErrors: [], wipeCounts: [], closed: 0 };
  const values = { mode: 'unauthenticated', apiKey: 'key' };
  const save = bindLifted(liftConstArrow('src/components/initialization/AccessSetup.tsx', 'save'), {
    busy: false,
    modeBlocked: false,
    apiKey: 'key',
    setError: (message) => calls.errors.push(message),
    setWipeError: (message) => calls.wipeErrors.push(message),
    setWipeCount: (count) => calls.wipeCounts.push(count),
    ApiError,
    ACCOUNT_WIPE_CONFIRM_KEY: CONFIRM_KEY,
    t: (key) => key,
    needsLocalPassword: false,
    needsSignIn: false,
    setBusy: () => undefined,
    authService: {
      configureAccess: async (request) => {
        calls.requests.push(request);
        return answer(request);
      }
    },
    authenticationSetupRequired: false,
    signInModeFromEnvironment: false,
    oidc: false,
    credentialsNeeded: false,
    selectedConnected: undefined,
    allowedSubjects: '',
    mode: 'unauthenticated',
    acknowledged: true,
    setPassword: () => undefined,
    setClientSecret: () => undefined,
    setPrivateKey: () => undefined,
    setApiKey: () => undefined,
    refreshAuth: async () => undefined,
    refreshSetupStatus: async () => undefined,
    changeStep: () => undefined,
    startTest: async () => undefined,
    pendingLoginKind: null,
    onClose: () => {
      calls.closed += 1;
    },
    getErrorMessage: (error) => error.message,
    formValues: values,
    formValuesRef: { current: values }
  });
  return { save, calls };
};

test('an unconfirmed switch opens the confirm with the count the server gives, and no error', async () => {
  const { save, calls } = saveHarness(() => {
    throw new ApiError({
      stageKey: CONFIRM_KEY,
      error: 'Turning sign-in off deletes all 3 accounts. Confirm to continue',
      context: { count: 3 }
    });
  });

  await save();

  assert.equal(calls.requests[0].confirmedAccountCount, undefined);
  assert.deepEqual(calls.wipeCounts, [3]);
  assert.deepEqual(calls.errors, [null], 'the confirm request was shown as an error');
  assert.equal(calls.closed, 0);
});

test('Confirm sends the confirmation with the count it showed, then closes the confirm and the settings', async () => {
  const { save, calls } = saveHarness(() => ({
    requiresLoginTest: false,
    requiresOidcTest: false
  }));

  await save(3);

  assert.equal(calls.requests[0].confirmedAccountCount, 3);
  assert.equal(
    'confirmAccountWipe' in calls.requests[0],
    false,
    'the request still sends the old flag'
  );
  assert.deepEqual(calls.wipeCounts, [null]);
  assert.equal(calls.closed, 1);
});

test('a Confirm answered with a new count keeps the confirm open with that count', async () => {
  const { save, calls } = saveHarness(() => {
    throw new ApiError({
      stageKey: CONFIRM_KEY,
      error: 'Turning sign-in off deletes all 4 accounts. Confirm to continue',
      context: { count: 4 }
    });
  });

  await save(3);

  assert.equal(calls.requests[0].confirmedAccountCount, 3);
  assert.deepEqual(calls.wipeCounts, [4]);
  assert.equal(calls.closed, 0);
});

test('a Confirm that fails shows its message inside the confirm', async () => {
  const { save, calls } = saveHarness(() => {
    throw new Error('The database is unavailable');
  });

  await save(3);

  assert.deepEqual(calls.wipeErrors, [null, 'The database is unavailable']);
  assert.deepEqual(calls.errors, [null], 'the failure was shown behind the confirm');
  assert.deepEqual(calls.wipeCounts, [], 'the confirm closed on a failure');
});
