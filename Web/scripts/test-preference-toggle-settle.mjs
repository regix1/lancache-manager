import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftConstArrow, liftHookCallback } from './transpile-module.mjs';

/**
 * Two switches flipped inside one round trip, and a switch whose save is refused.
 *
 * A broadcast about ANY one preference carries the whole row, so it also carries the server's older
 * value for every other key. Flipping a second switch before the first one's broadcast lands used
 * to put that second switch straight back, and a save the server refused left the switch showing a
 * value nobody stored, with nothing said about it.
 *
 * Both the settle and the write handler ship inside components, so each is lifted out of its file
 * and run against stubs for the names it reaches for.
 */

const preferenceTypesUrl = await compileToUrl('../src/types/userPreferences.ts');
const { OPTIMISTIC_TOGGLE_KEYS } = await import(preferenceTypesUrl);

const { setPendingPreference, dropPendingPreference, preferPendingValue } = await import(
  await compileToUrl('../src/utils/pendingPreferences.ts', {
    '../types/userPreferences.ts': preferenceTypesUrl
  })
);

/** The settle exactly as the context runs it, over the real pending store. */
const settlePendingToggles = bindLifted(
  liftConstArrow('src/contexts/SessionPreferencesContext.tsx', 'settlePendingToggles'),
  { OPTIMISTIC_TOGGLE_KEYS, preferPendingValue }
);

/** A server row where every switch is off. */
const allOff = () => ({
  sharpCorners: false,
  disableTooltips: false,
  disableStickyNotifications: false,
  picsAlwaysVisible: false,
  showDatasourceLabels: false,
  selectedTheme: null
});

test('a switch flipped inside another switch round trip is not written back', () => {
  setPendingPreference('disableTooltips', true);
  try {
    // Sharp corners was saved first, so its broadcast carries the row as the server knew it before
    // tooltips were touched at all.
    const settled = settlePendingToggles({ ...allOff(), sharpCorners: true });
    assert.equal(settled.disableTooltips, true);
  } finally {
    dropPendingPreference('disableTooltips');
  }
});

test('a broadcast still applies to every key with no save in flight', () => {
  setPendingPreference('disableTooltips', true);
  try {
    const settled = settlePendingToggles({
      ...allOff(),
      sharpCorners: true,
      picsAlwaysVisible: true
    });
    assert.equal(settled.sharpCorners, true);
    assert.equal(settled.picsAlwaysVisible, true);
    assert.equal(settled.disableTooltips, true);
  } finally {
    dropPendingPreference('disableTooltips');
  }
});

test('with nothing in flight the broadcast is taken whole', () => {
  const row = { ...allOff(), disableTooltips: true, showDatasourceLabels: true };
  assert.deepEqual(settlePendingToggles(row), row);
});

test('a released switch stops outranking the next broadcast', () => {
  setPendingPreference('disableTooltips', true);
  dropPendingPreference('disableTooltips');
  assert.equal(settlePendingToggles(allOff()).disableTooltips, false);
});

/** The write handler as DisplayPreferences runs it, with every name it reaches for supplied. */
const buildWriter = () => {
  const calls = { optimistic: [], errors: [], local: [] };
  const writePreference = bindLifted(
    liftHookCallback(
      'src/components/features/management/sections/DisplayPreferences.tsx',
      'useCallback',
      'setPendingPreference'
    ),
    {
      setOptimisticPreference: (key, value) => calls.optimistic.push([key, value]),
      setPendingPreference,
      dropPendingPreference,
      notifyError: (message) => calls.errors.push(message),
      t: (key) => key
    }
  );
  return { writePreference, calls };
};

test('a refused save puts the switch back and says so', async () => {
  const { writePreference, calls } = buildWriter();
  const local = [];

  await writePreference(
    {
      key: 'disableTooltips',
      previous: false,
      setLocal: (value) => local.push(value),
      save: async () => false
    },
    true
  );

  assert.deepEqual(local, [true, false]);
  assert.deepEqual(calls.optimistic, [
    ['disableTooltips', true],
    ['disableTooltips', false]
  ]);
  assert.equal(calls.errors.length, 1);
  // Released either way, or a refused save would keep outranking every later broadcast.
  assert.equal(preferPendingValue('disableTooltips', false), false);
});

test('a save the server took leaves the picked value alone and says nothing', async () => {
  const { writePreference, calls } = buildWriter();
  const local = [];

  await writePreference(
    {
      key: 'disableTooltips',
      previous: false,
      setLocal: (value) => local.push(value),
      save: async () => true
    },
    true
  );

  assert.deepEqual(local, [true]);
  assert.deepEqual(calls.optimistic, [['disableTooltips', true]]);
  assert.deepEqual(calls.errors, []);
  assert.equal(preferPendingValue('disableTooltips', false), false);
});

test('the picked value is held for the length of the save', async () => {
  const { writePreference } = buildWriter();
  const local = [];
  let heldDuringSave = null;

  await writePreference(
    {
      key: 'disableTooltips',
      previous: false,
      setLocal: (value) => local.push(value),
      save: async () => {
        heldDuringSave = preferPendingValue('disableTooltips', false);
        return true;
      }
    },
    true
  );

  assert.equal(heldDuringSave, true);
});
