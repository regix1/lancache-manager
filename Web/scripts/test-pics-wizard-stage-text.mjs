import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  moduleUrl,
  translator
} from './transpile-module.mjs';

/**
 * The setup wizard's depot step learns how a Steam depot scan is going from DepotMappingProgress.
 * The server sends each step as a stage key with its numbers; `status` is the typed operation
 * status and the context holds no `status` key. Before that change the server also sent an English
 * line for the log as `status` and `context.status`, and the payloads below keep that older shape
 * on purpose: a reader using the Chinese locale must see the stage in Chinese, so the step has to
 * draw the stage key through the translator and never print a `status` string it is given.
 *
 * The shipped progress handler folds each event into the context's state, and the real step
 * renders that state; only the step's UI children, icons and services are stubbed.
 */

const handleProgressSource = liftConstArrow(
  'src/contexts/PicsProgressContext.tsx',
  'handleDepotMappingProgress'
);

// The automatic JSX runtime emits a double-quoted import that the alias rewrite does not match,
// so the classic transform is used and its React.createElement resolves through this global.
globalThis.React = React;
const progressBox = { progress: null };
globalThis.picsWizardProgressBox = progressBox;
const passThrough = (name) =>
  `export const ${name} = (props) => React.createElement('div', null, props.caption ?? null, props.children ?? null);`;

const chinese = translator('zh');
const { PicsProgressStep } = await import(
  await compileToUrl(
    '../src/components/initialization/steps/PicsProgressStep.tsx',
    {
      react: import.meta.resolve('react'),
      'react-i18next': moduleUrl(
        'export const useTranslation = () => ({ t: globalThis.picsWizardTranslate });'
      ),
      'lucide-react': moduleUrl('export const Database = () => null, CheckCircle = Database;'),
      '@components/ui/Button': moduleUrl(passThrough('Button')),
      '@components/ui/ProgressBar': moduleUrl(passThrough('ProgressBar')),
      '@components/common/LoadingSpinner': moduleUrl('export default () => null;'),
      '@contexts/usePicsProgress': moduleUrl(
        'export const usePicsProgress = () => ({ progress: globalThis.picsWizardProgressBox.progress });'
      ),
      '@services/api.service': moduleUrl(
        'export default { cancelSteamKitRebuild: async () => undefined };'
      ),
      '@utils/error': moduleUrl('export const getErrorMessage = (error) => String(error);')
    },
    { jsx: typescript.JsxEmit.React }
  )
);
globalThis.picsWizardTranslate = chinese;

// What GET /api/depots/rebuild/progress answered while the scan ran, before the first event.
const polledProgress = {
  isProcessing: true,
  status: 'processing',
  totalApps: 5000,
  processedApps: 0,
  totalBatches: 10,
  processedBatches: 0,
  progressPercent: 0,
  depotMappingsFound: 1200,
  depotMappingsFoundInSession: 0,
  crawlIntervalHours: 1,
  crawlIncrementalMode: true,
  isReady: true,
  isConnected: true,
  isLoggedOn: false,
  isWebApiAvailable: true,
  errorMessage: null
};

/**
 * One DepotMappingProgress payload in the shape `SteamKit2Service.Pics.cs` sent before the stage
 * key change: an English line as `status` and as `context.status`, next to the stage key and its
 * numbers. The server no longer sends that line; the payload keeps it so the step is checked
 * against printing any `status` string.
 */
const progressEvent = (status, stageKey, percentComplete, processedBatches) => {
  const context = {
    status,
    totalApps: 5000,
    processedApps: processedBatches * 500,
    totalBatches: 10,
    processedBatches,
    depotMappingsFound: 1200,
    remainingApps: 5000 - processedBatches * 500,
    totalMappings: 0,
    processedMappings: 0,
    mappingsApplied: 0,
    isReconnecting: false,
    reconnectAttempt: null,
    maxReconnectAttempts: null,
    errorDetail: null,
    scanMode: 'incremental'
  };
  return {
    serviceKey: 'depotMapping',
    operationId: '6d1f0c3e-2b7a-4f7e-9a51-0b8c2d4e6f10',
    status,
    stageKey,
    percentComplete,
    context,
    scanMode: 'incremental',
    isLoggedOn: false,
    timestamp: '2026-11-11T10:00:00Z',
    totalApps: context.totalApps,
    processedApps: context.processedApps,
    totalBatches: context.totalBatches,
    processedBatches,
    depotMappingsFound: context.depotMappingsFound,
    failedBatches: 0,
    remainingApps: context.remainingApps,
    isReconnecting: false,
    reconnectAttempt: null,
    maxReconnectAttempts: null,
    processedMappings: 0,
    totalMappings: 0,
    mappingsApplied: 0
  };
};

const stages = [
  {
    event: progressEvent('Scanning Steam PICS data', 'signalr.depotMapping.batchProgress', 22.5, 3),
    shown: chinese('signalr.depotMapping.batchProgress', { processedBatches: 3, totalBatches: 10 })
  },
  {
    event: progressEvent('Saving depot mappings to JSON...', 'signalr.depotMapping.saving', 80, 10),
    shown: chinese('signalr.depotMapping.saving')
  }
];

test('the setup wizard shows a depot scan stage in the reader language, not the server English status', () => {
  for (const { event, shown } of stages) {
    let state = { ...polledProgress };
    const handleDepotMappingProgress = bindLifted(handleProgressSource, {
      setProgress: (next) => {
        state = typeof next === 'function' ? next(state) : next;
      },
      createDefaultProgress: () => ({ ...polledProgress, isProcessing: false, status: 'idle' })
    });
    handleDepotMappingProgress(event);
    progressBox.progress = state;

    const html = renderToStaticMarkup(
      React.createElement(PicsProgressStep, { onComplete: () => undefined })
    );

    assert.equal(
      html.includes(event.status),
      false,
      `a Chinese reader sees the server English status "${event.status}" for stage ${event.stageKey}`
    );
    assert.equal(
      html.includes(shown),
      true,
      `the wizard does not show the translated stage "${shown}" for ${event.stageKey}`
    );
  }
});
