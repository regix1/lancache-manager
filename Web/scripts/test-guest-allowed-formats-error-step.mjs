// Saving the guests' allowed clock formats is two requests when the current default clock is left out
// of the new list: the list first, then the default moved to the first allowed clock. When only the
// second fails, the server has kept the list, so the page must not say the list failed: its error
// names the default clock it could not move, and the page shows the list the server saved. The
// handler and the clock save that ship in GuestConfiguration.tsx run here with the network faked.
import assert from 'node:assert/strict';
import test from 'node:test';
import { clockFromTimeSetting } from '../src/utils/pendingPreferences.ts';
import { bindLifted, liftConstArrow, translator } from './transpile-module.mjs';

const componentPath = 'src/components/features/user/GuestConfiguration.tsx';
const translate = translator('en');
const listPath = '/api/system/default-guest-preferences/allowed-time-formats';
const clockPath = '/api/system/default-guest-preferences/clock';
const ListFails = 'the allowed formats request fails';
const ClockFails = 'the list is saved and moving the default clock fails';
const NothingFails = 'the list is saved and the default clock is still allowed';
// The new list leaves out the 12-hour local default whenever the default has to move.
const formats = ['server-24h'];

const saveAllowedFormats = async (outcome) => {
  const requests = [];
  const errors = [];
  const errorKeys = [];
  const applied = [];
  const busyStates = [];
  const answers = {
    [listPath]: () => (outcome === ListFails ? { ok: false, status: 500 } : { ok: true }),
    [clockPath]: (body) =>
      outcome === ClockFails
        ? { ok: false, status: 500 }
        : { ok: true, json: async () => ({ clock: JSON.parse(body) }) }
  };
  const shared = {
    defaultPrefsPushesRef: { current: new Map() },
    fetch: async (path, options) => {
      requests.push(path);
      return { status: 200, ...answers[path](options.body) };
    },
    ApiService: {
      getJsonFetchOptions: (body, options) => ({ ...options, body: JSON.stringify(body) })
    },
    assertOk: async (response) => {
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response;
    },
    applyDefaultPrefsReply: (reply) => applied.push(reply)
  };
  const updateDefaultTimeFormat = bindLifted(
    liftConstArrow(componentPath, 'updateDefaultTimeFormat'),
    { ...shared, clockFromTimeSetting }
  );
  const handleAllowedFormatsChange = bindLifted(
    liftConstArrow(componentPath, 'handleAllowedFormatsChange'),
    {
      ...shared,
      authMode: 'authenticated',
      setUpdatingAllowedFormats: (busy) => busyStates.push(busy),
      getCurrentDefaultFormat: () => (outcome === NothingFails ? 'server-24h' : 'local-12h'),
      updateDefaultTimeFormat,
      notifyError: (message) => errors.push(message),
      // The handler translates only the error it shows, so the keys it asks for are the popups' keys.
      t: (key, vars) => {
        errorKeys.push(key);
        return translate(key, vars);
      }
    }
  );

  await handleAllowedFormatsChange(formats);
  assert.deepEqual(busyStates, [true, false], 'the save must end with its busy flag cleared');
  const listReplies = applied.filter((reply) => 'allowedTimeFormats' in reply);
  return { requests, errors, errorKeys, listReplies };
};

const listError = translate('user.guest.errors.updateAllowedTimeFormats');

test(`when ${ListFails}, the page says the allowed formats failed`, async () => {
  const { requests, errors, listReplies } = await saveAllowedFormats(ListFails);
  assert.deepEqual(requests, [listPath]);
  assert.deepEqual(errors, [listError]);
  assert.deepEqual(listReplies, [], 'a list the server refused must not show as saved');
});

test(`when ${ClockFails}, the error names the default clock and the page shows the saved list`, async () => {
  const { requests, errors, errorKeys, listReplies } = await saveAllowedFormats(ClockFails);
  assert.deepEqual(requests, [listPath, clockPath]);
  assert.equal(errors.length, 1, `one error is shown, not ${JSON.stringify(errors)}`);
  assert.ok(
    errors[0] !== listError && /default/i.test(errors[0]),
    `the page said "${errors[0]}" though the server saved the allowed formats; the error must name the default clock it could not move`
  );
  assert.deepEqual(
    errorKeys,
    ['user.guest.errors.updateDefaultTimeFormat'],
    'the popup does not name the default clock save, which is the one that failed'
  );
  assert.deepEqual(
    listReplies,
    [{ allowedTimeFormats: ['server-24h'] }],
    'the server saved the allowed formats, but the page did not take the saved list'
  );
});

test(`when ${NothingFails}, nothing is moved and no error is shown`, async () => {
  const { requests, errors, listReplies } = await saveAllowedFormats(NothingFails);
  assert.deepEqual(requests, [listPath]);
  assert.deepEqual(errors, []);
  assert.deepEqual(listReplies, [{ allowedTimeFormats: ['server-24h'] }]);
});
