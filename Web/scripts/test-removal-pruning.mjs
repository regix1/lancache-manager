import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * Exercises the real useCompletedRemovalPruning compiled from product source.
 *
 * A removal that finished on one datasource and failed on another completes with `entityKept`:
 * files remain, so the page must keep the game or service listed instead of pruning it.
 */

const loadHook = async (nonce) => {
  const reactUrl = moduleUrl(`// ${nonce}
export const __effects = [];
export const useCallback = (fn) => fn;
export const useEffect = (fn) => {
  __effects.push(fn);
};`);

  const signalRUrl = moduleUrl(`// ${nonce}
export const handlers = {};
export const useSignalR = () => ({
  on: (name, fn) => {
    handlers[name] = fn;
  },
  off: () => {}
});`);

  const apiUrl = moduleUrl(`// ${nonce}
export default {};`);

  const constantsUrl = moduleUrl(`// ${nonce}
export const FAILED_TO_REMOVE_GAME_I18N_KEY = 'failed';
export const FULL_PROGRESS_PERCENT = 100;`);

  const timeoutUrl = moduleUrl(`// ${nonce}
export const useTimeoutCallback = () => () => {};`);

  const detectionDataUrl = moduleUrl(`// ${nonce}
export const pruneGamesByRemovalTarget = (prev) => prev;
export const pruneServicesByRemovalTarget = (prev) => prev;`);

  const filtersUrl = moduleUrl(`// ${nonce}
export const FULL_REMOVAL_REFRESH_DELAY_MS = 0;`);

  const entityUrl = moduleUrl(`// ${nonce}
export const classifyGameFromCacheInfo = () => 'steam';`);

  const hookUrl = await compileToUrl(
    '../src/components/features/management/game-detection/cacheRemovalHelpers.ts',
    {
      react: reactUrl,
      '@services/api.service': apiUrl,
      '@contexts/notifications/constants': constantsUrl,
      '@contexts/SignalRContext/useSignalR': signalRUrl,
      '@/hooks/useTimeoutCallback': timeoutUrl,
      './cacheDetectionData': detectionDataUrl,
      './cacheEntityFilters': filtersUrl,
      './gameRemovalEntity': entityUrl
    }
  );

  const react = await import(reactUrl);
  const signalR = await import(signalRUrl);
  const { useCompletedRemovalPruning } = await import(hookUrl);

  const setGamesCalls = [];
  const setServicesCalls = [];
  useCompletedRemovalPruning({
    setGames: (update) => setGamesCalls.push(update),
    setServices: (update) => setServicesCalls.push(update)
  });
  react.__effects.forEach((effect) => effect());

  return { handlers: signalR.handlers, setGamesCalls, setServicesCalls };
};

const gameEvent = (overrides = {}) => ({
  success: true,
  gameAppId: 570,
  epicAppId: null,
  gameName: 'Dota 2',
  operationId: 'a',
  ...overrides
});

const serviceEvent = (overrides = {}) => ({
  success: true,
  serviceName: 'steam',
  stageKey: 'x',
  message: '',
  operationId: 'b',
  ...overrides
});

test('a removal that finished everywhere prunes the game and the service', async () => {
  const { handlers, setGamesCalls, setServicesCalls } = await loadHook('finished');

  handlers.GameRemovalComplete(gameEvent());
  handlers.ServiceRemovalComplete(serviceEvent());

  assert.equal(setGamesCalls.length, 1);
  assert.equal(setServicesCalls.length, 1);
});

test('a removal that kept files on a failed datasource leaves the game and the service listed', async () => {
  const { handlers, setGamesCalls, setServicesCalls } = await loadHook('kept');

  handlers.GameRemovalComplete(gameEvent({ entityKept: true }));
  handlers.ServiceRemovalComplete(serviceEvent({ entityKept: true }));

  assert.equal(setGamesCalls.length, 0);
  assert.equal(setServicesCalls.length, 0);
});
