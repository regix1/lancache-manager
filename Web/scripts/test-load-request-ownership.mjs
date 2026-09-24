import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, liftConstArrow, liftHookCallback, parseSource } from './transpile-module.mjs';

/**
 * A section's mount read, its reconnect read, an event reload and Retry can all be on the wire at
 * once, and the server can answer them out of order. Each case lifts the loader that ships, holds
 * two reads, answers the newer one, then fails the older one: the section keeps the newer answer
 * and shows no error box over controls that loaded.
 */

const GUEST = 'src/components/features/user/GuestConfiguration.tsx';
const ACCESS = 'src/components/features/user/AccessSecurityCard.tsx';
const THEMES = 'src/components/features/management/theme/ThemeManager.tsx';

/** Requests the test answers or fails later, in any order. */
const heldReads = () => {
  const reads = [];
  const start = () =>
    new Promise((resolve, reject) => {
      reads.push({ resolve, reject });
    });
  return { reads, start };
};

const settle = () => new Promise((resolve) => setImmediate(resolve));

test('an older default guest preferences read that fails last leaves the card in place', async () => {
  const state = { preferences: [], errors: [], loading: [] };
  const { reads, start } = heldReads();
  const loadDefaultGuestPreferences = bindLifted(
    liftConstArrow(GUEST, 'loadDefaultGuestPreferences'),
    {
      defaultPrefsRequestRef: { current: 0 },
      fetch: start,
      ApiService: { getFetchOptions: () => ({}) },
      assertOk: async (response) => response,
      TIME_SETTING_VALUES: ['server-24h'],
      setLoadingDefaultPrefs: (value) => state.loading.push(value),
      setDefaultGuestPreferences: (value) => state.preferences.push(value),
      setDefaultPrefsError: (value) => state.errors.push(value),
      getErrorMessage: (error) => error.message
    }
  );

  const older = loadDefaultGuestPreferences();
  const newer = loadDefaultGuestPreferences();
  reads[1].resolve({ json: async () => ({ sharpCorners: true }) });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.equal(state.preferences.length, 1);
  assert.equal(state.preferences[0].sharpCorners, true);
  assert.deepEqual(state.errors, [null], 'the stale failure never replaces the card');
  assert.equal(state.loading.at(-1), false);
});

test('an older prefill batch that fails last leaves every service panel in place', async () => {
  const services = [
    { id: 'steam', guestConfigPath: '/steam' },
    { id: 'epic', guestConfigPath: '/epic' }
  ];
  const state = { configs: {}, loading: {}, errors: [] };
  const { reads, start } = heldReads();
  const prefillConfigRequestRef = { current: 0 };
  const setPrefillConfigError = (value) => state.errors.push(value);
  const loadPrefillConfig = bindLifted(
    liftHookCallback(GUEST, 'useCallback', 'guestConfigPath, ApiService.getFetchOptions()'),
    {
      prefillConfigRequestRef,
      fetch: start,
      ApiService: { getFetchOptions: () => ({}) },
      assertOk: async (response) => response,
      toGuestPrefillConfig: (service, config) => config,
      setPrefillConfigs: (update) => {
        state.configs = update(state.configs);
      },
      setLoadingPrefillConfigs: (update) => {
        state.loading = update(state.loading);
      },
      setPrefillConfigError,
      getErrorMessage: (error) => error.message
    }
  );
  const loadPrefillConfigs = bindLifted(
    liftHookCallback(GUEST, 'useCallback', '++prefillConfigRequestRef.current'),
    {
      prefillConfigRequestRef,
      PREFILL_SERVICES: services,
      loadPrefillConfig,
      setPrefillConfigError
    }
  );

  // The mount batch is still out when the reconnect batch starts and answers.
  loadPrefillConfigs();
  loadPrefillConfigs();
  await settle();
  reads[2].resolve({ json: async () => ({ enabledByDefault: true }) });
  reads[3].resolve({ json: async () => ({ enabledByDefault: false }) });
  await settle();
  reads[0].reject(new Error('stale steam read failed'));
  reads[1].reject(new Error('stale epic read failed'));
  await settle();

  assert.deepEqual(state.configs, {
    steam: { enabledByDefault: true },
    epic: { enabledByDefault: false }
  });
  assert.deepEqual(state.errors, [null, null], 'a failure from the older batch never lands');
  assert.deepEqual(state.loading, { steam: false, epic: false });
});

test('the guest defaults mount effect lists the prefill batch it starts', () => {
  const source = parseSource(GUEST).text;

  assert.doesNotMatch(
    source,
    /eslint-disable-next-line react-hooks\/exhaustive-deps/,
    'the dependency rule checks this file with nothing switched off'
  );
  assert.match(source, /handlePrefillConfigChanged,\s*loadPrefillConfigs\s*\]\);/);
});

test('an older guest session duration read that fails last shows no error box', async () => {
  const state = { durations: [], errors: [] };
  const { reads, start } = heldReads();
  const fetchGuestDuration = bindLifted(
    liftHookCallback(ACCESS, 'useCallback', 'getGuestSessionDuration'),
    {
      durationRequestRef: { current: 0 },
      ApiService: { getGuestSessionDuration: start },
      setState: (value) => state.durations.push(value),
      setLoadError: (value) => state.errors.push(value),
      getErrorMessage: (error) => error.message
    }
  );

  const older = fetchGuestDuration();
  const newer = fetchGuestDuration();
  reads[1].resolve({ durationHours: 12, source: 'ui' });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.durations, [{ durationHours: 12, source: 'ui' }]);
  assert.deepEqual(state.errors, [null], 'the stale failure never reaches the card');
});

test('an older theme list read that fails last shows no error box above the list', async () => {
  const state = { themes: [], errors: [], loading: [] };
  const { reads, start } = heldReads();
  const loadThemes = bindLifted(
    liftHookCallback(THEMES, 'useCallback', 'themeService.loadThemes'),
    {
      themesRequestRef: { current: 0 },
      themeService: { loadThemes: start },
      setThemes: (value) => state.themes.push(value),
      setLoadError: (value) => state.errors.push(value),
      setLoading: (value) => state.loading.push(value),
      getErrorMessage: (error) => error.message
    }
  );

  const older = loadThemes();
  const newer = loadThemes();
  reads[1].resolve({ themes: [{ meta: { id: 'dark-default' } }], loadError: null });
  await newer;
  reads[0].reject(new Error('stale read failed'));
  await older;

  assert.deepEqual(state.themes, [[{ meta: { id: 'dark-default' } }]]);
  assert.equal(state.errors.includes('stale read failed'), false);
  assert.equal(state.errors.at(-1), null, 'the stale failure never reaches the section');
  assert.equal(state.loading.at(-1), false);
});

test('the community theme error box carries its own spacing, so a hidden box leaves none', () => {
  const source = parseSource(
    'src/components/features/management/theme/CommunityThemeImporter.tsx'
  ).text;
  const box = source.match(/<ErrorBlock\b[\s\S]*?\/>/);

  assert.ok(box, 'the importer renders no error box');
  // The gap is for content under the box; as the body's last child it adds none.
  assert.ok(
    box[0].includes('className="mb-4 last:mb-0"'),
    'the box is missing its own gap, or keeps it with nothing below'
  );
  assert.doesNotMatch(source, /<div className="mb-4">\s*<ErrorBlock/);
});
