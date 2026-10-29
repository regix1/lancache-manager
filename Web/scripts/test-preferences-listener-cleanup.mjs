import assert from 'node:assert/strict';
import test from 'node:test';
import { bindLifted, compileToUrl, liftHookCallback, moduleUrl } from './transpile-module.mjs';

/**
 * The app re-runs the effect that wires the preference listeners every time the connection state
 * changes. Each run has to take its listeners off first, or every reconnect stacks another pair
 * on the same events and a single reset or revocation is handled once per reconnect so far.
 */

const loadPreferencesService = async () => {
  const constants = moduleUrl(
    `export const API_BASE = '/api';
     export const APP_EVENTS = { PREFERENCES_RESET: 'preferences-reset' };`
  );
  const url = await compileToUrl('../src/services/preferences.service.ts', {
    '../utils/constants': constants,
    '@utils/constants': constants,
    './api.service': moduleUrl('export default class ApiService {}'),
    '@/types/userPreferences': await compileToUrl('../src/types/userPreferences.ts')
  });
  const { default: preferencesService } = await import(url);
  return preferencesService;
};

/** One connection's handler table, shared by every context value that wraps it. */
const connection = () => {
  const handlers = new Map();
  return {
    handlers,
    value: () => ({
      on: (name, handler) => {
        if (!handlers.has(name)) handlers.set(name, new Set());
        handlers.get(name).add(handler);
      },
      off: (name, handler) => handlers.get(name)?.delete(handler)
    }),
    count: (name) => handlers.get(name)?.size ?? 0
  };
};

test('each run of the listener effect leaves one pair of preference listeners on the connection', async () => {
  const preferencesService = await loadPreferencesService();
  const effect = liftHookCallback(
    'src/App.tsx',
    'useEffect',
    'preferencesService.setupSignalRListener(signalR)'
  );
  const hub = connection();
  const themeService = { setupPreferenceListeners: () => undefined };
  const run = (signalR) => bindLifted(effect, { preferencesService, themeService, signalR })();

  // Three connection-state changes: a new context value each time, the same connection under it.
  let stop = run(hub.value());
  for (let change = 0; change < 2; change += 1) {
    stop?.();
    stop = run(hub.value());
  }

  assert.equal(hub.count('UserPreferencesReset'), 1);
  assert.equal(hub.count('UserSessionRevoked'), 1);

  stop?.();
  assert.equal(hub.count('UserPreferencesReset'), 0);
  assert.equal(hub.count('UserSessionRevoked'), 0);
});
