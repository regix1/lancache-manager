import assert from 'node:assert/strict';
import test from 'node:test';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  translator
} from './transpile-module.mjs';

// The full-scan prompt's two buttons ("Run full scan" and "Download from GitHub") close the prompt
// and ask the server for a depot run. When the server refuses, the click is answered: one failed
// popup naming the depot mapping run, with the server's reason as its second line when the server
// gave one, and the prompt's action flag is freed so the prompt can come back. A scan that started
// raises nothing and frees the flag too, so a missed end event cannot keep the prompt away, while a
// refresh during the run still keeps the prompt closed because the server reports the run. The handlers are the ones
// `src/App.tsx` ships; the popup comes from the real `useErrorHandler`.

const appPath = 'src/App.tsx';
const cancelSource = liftHookCallback(
  appPath,
  'useCallback',
  'fullScanRequestRef.current?.abort()'
);
const refreshSource = liftHookCallback(appPath, 'useCallback', 'ApiService.getSchedules');
const t = translator('en');
const depotService = t('management.schedules.services.depotMapping.displayName');

globalThis.fullScanConflictTranslator = { t };
const i18nStub = moduleUrl('export default globalThis.fullScanConflictTranslator;');
const apiErrorUrl = await compileToUrl('../src/services/apiError.ts', {
  '@/i18n': i18nStub,
  '@utils/constants': moduleUrl('export const APP_EVENTS = {};')
});
const { ApiError } = await import(apiErrorUrl);
const errorUrl = await compileToUrl('../src/utils/error.ts', {
  '@/i18n': i18nStub,
  '../services/apiError': apiErrorUrl
});
const { isAbortError } = await import(errorUrl);

globalThis.fullScanConflictPopups = [];
const { useErrorHandler } = await import(
  await compileToUrl('../src/hooks/useErrorHandler.ts', {
    react: moduleUrl('export const useCallback = (fn) => fn;'),
    '@contexts/notifications': moduleUrl(
      'export const useNotifications = () => ({ addNotification: (popup) => globalThis.fullScanConflictPopups.push(popup) });'
    ),
    '@utils/error': errorUrl
  })
);

// The answer a depot run gets while another one holds the lock.
const conflict = () =>
  new ApiError({
    message: 'Operation already in progress',
    status: 409,
    kind: 'conflict',
    body: { code: 'OPERATION_CONFLICT', stageKey: 'errors.conflict.duplicate' }
  });

/** Clicks one of the prompt's buttons with the server answering `answer`; returns what the user sees. */
const click = async (handlerName, answer) => {
  globalThis.fullScanConflictPopups.length = 0;
  const fullScanActionRunningRef = { current: false };
  const serverCall = () => answer();
  const ignore = () => undefined;
  const handler = bindLifted(liftConstArrow(appPath, handlerName), {
    cancelFullScanCheck: ignore,
    fullScanActionRunningRef,
    setShowFullScanRequiredModal: ignore,
    refreshFullScanPrompt: () => Promise.resolve(),
    ApiService: {
      triggerSteamKitRebuild: serverCall,
      downloadPrecreatedDepotData: serverCall
    },
    ApiError,
    isAbortError,
    notifyError: useErrorHandler().notifyError,
    t,
    console: { error: ignore, warn: ignore, log: ignore }
  });
  await handler();
  return {
    popups: [...globalThis.fullScanConflictPopups],
    actionRunning: fullScanActionRunningRef.current
  };
};

for (const [button, handlerName] of [
  ['Run full scan', 'handleRunFullScan'],
  ['Download from GitHub', 'handleDownloadFromGitHub']
]) {
  test(`a refused "${button}" click shows one depot popup with the server's reason and frees the prompt`, async () => {
    const seen = await click(handlerName, () => Promise.reject(conflict()));
    assert.equal(
      seen.popups.length,
      1,
      `a "${button}" click the server refused with 409 showed ${seen.popups.length} popups; the user is told nothing`
    );
    const [popup] = seen.popups;
    assert.equal(popup.status, 'failed');
    assert.ok(
      popup.message.includes(depotService),
      `the popup reads "${popup.message}", which does not name ${depotService}`
    );
    assert.equal(
      popup.error,
      t('errors.conflict.duplicate'),
      'the popup does not carry the server reason'
    );
    assert.equal(
      seen.actionRunning,
      false,
      `after the refused "${button}" click the prompt's action flag is still set, so the prompt never comes back`
    );
  });
}

test('a full scan the server did not start shows one depot popup with no reason line and frees the prompt', async () => {
  const seen = await click('handleRunFullScan', () =>
    Promise.resolve({ started: false, message: 'A depot mapping run is already in progress' })
  );
  assert.equal(
    seen.popups.length,
    1,
    `a "Run full scan" click answered 202 with started: false showed ${seen.popups.length} popups; the user is told nothing`
  );
  const [popup] = seen.popups;
  assert.ok(
    popup.message.includes(depotService),
    `the popup reads "${popup.message}", which does not name ${depotService}`
  );
  assert.equal(
    popup.error,
    undefined,
    'a start the server declined without an error has no reason line'
  );
  assert.equal(
    seen.actionRunning,
    false,
    'the prompt action flag is still set after nothing started'
  );
});

test('a full scan that started shows nothing, frees the prompt action flag, and a refresh while it runs keeps the prompt closed', async () => {
  const seen = await click('handleRunFullScan', () =>
    Promise.resolve({ started: true, message: 'Depot rebuild started' })
  );
  assert.deepEqual(seen.popups, []);

  // The Schedules row's button asks for the prompt while the scan runs: the server still asks for a
  // full scan and reports the depot run as running.
  const shown = [];
  const ignore = () => undefined;
  const bindings = {
    authMode: 'authenticated',
    fullScanActionRunningRef: { current: seen.actionRunning },
    fullScanRequestRef: { current: null },
    wasModalDismissed: () => false,
    setShowFullScanRequiredModal: (value) => shown.push(value),
    setFullScanModalChangeGap: ignore,
    setFullScanModalEstimatedApps: ignore,
    ApiService: {
      getSchedules: () =>
        Promise.resolve([
          {
            key: 'depotMapping',
            isRunning: true,
            pendingFullScan: { changeGap: 1234, estimatedAppsToScan: 2468 }
          }
        ])
    },
    notifyError: ignore,
    t
  };
  bindings.cancelFullScanCheck = bindLifted(cancelSource, bindings);
  await bindLifted(refreshSource, bindings)(true);
  assert.ok(
    !shown.includes(true),
    'a refresh while the started scan runs opened the Full Scan Required prompt'
  );
  assert.equal(
    seen.actionRunning,
    false,
    'the prompt action flag stays set after the scan started, so a missed end event leaves the prompt unable to come back'
  );
});
