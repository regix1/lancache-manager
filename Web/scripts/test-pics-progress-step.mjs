import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import typescript from 'typescript';
import {
  bindLifted,
  compileToUrl,
  liftConstArrow,
  liftHookCallback,
  moduleUrl,
  translator
} from './transpile-module.mjs';

/**
 * The setup wizard's depot step says which step a Steam depot scan is on and moves on by itself
 * once the scan is done. Both come from typed values: the step from the stage key the last depot
 * event named, the phase the progress poll answers, and the run status the completion carries. A
 * run that fails or is canceled after its bar reached the end must not read as done.
 */

const stepPath = 'src/components/initialization/steps/PicsProgressStep.tsx';
const contextPath = 'src/contexts/PicsProgressContext.tsx';
const finishedEffectSource = liftHookCallback(stepPath, 'useEffect', 'setIsComplete(true)');
const handleCompleteSource = liftConstArrow(contextPath, 'handleDepotMappingComplete');

globalThis.React = React;
const progressBox = { progress: null };
globalThis.depotStepProgressBox = progressBox;
const passThrough = (name) =>
  `export const ${name} = (props) => React.createElement('div', null, props.caption ?? null, props.children ?? null);`;

const chinese = translator('zh');
globalThis.depotStepTranslate = chinese;
const { PicsProgressStep } = await import(
  await compileToUrl(
    '../src/components/initialization/steps/PicsProgressStep.tsx',
    {
      react: import.meta.resolve('react'),
      'react-i18next': moduleUrl(
        'export const useTranslation = () => ({ t: globalThis.depotStepTranslate });'
      ),
      'lucide-react': moduleUrl('export const Database = () => null, CheckCircle = Database;'),
      '@components/ui/Button': moduleUrl(passThrough('Button')),
      '@components/ui/ProgressBar': moduleUrl(passThrough('ProgressBar')),
      '@components/common/LoadingSpinner': moduleUrl('export default () => null;'),
      '@contexts/usePicsProgress': moduleUrl(
        'export const usePicsProgress = () => ({ progress: globalThis.depotStepProgressBox.progress });'
      ),
      '@services/api.service': moduleUrl(
        'export default { cancelSteamKitRebuild: async () => undefined };'
      ),
      '@utils/error': moduleUrl('export const getErrorMessage = (error) => String(error);')
    },
    { jsx: typescript.JsxEmit.React }
  )
);

/** What GET /api/depots/rebuild/progress answers while a scan runs. */
const polled = (overrides) => ({
  isProcessing: true,
  status: 'processing',
  totalApps: 5000,
  processedApps: 2500,
  totalBatches: 10,
  processedBatches: 5,
  progressPercent: 40,
  depotMappingsFound: 1200,
  crawlIntervalHours: 1,
  crawlIncrementalMode: true,
  errorMessage: null,
  ...overrides
});

const render = (progress) => {
  progressBox.progress = progress;
  return renderToStaticMarkup(
    React.createElement(PicsProgressStep, { onComplete: () => undefined })
  );
};

/** Runs the step's shipped "finished" effect against one state and says whether it advanced. */
const advances = (progress) => {
  let finished = false;
  const effect = bindLifted(finishedEffectSource, {
    progress,
    isComplete: false,
    setIsComplete: (value) => {
      finished = value;
    },
    completeTimerRef: { current: null },
    setTimeout: () => 0,
    onComplete: () => undefined
  });
  effect();
  return finished;
};

/** Folds one DepotMappingComplete into a state through the context's shipped handler. */
const complete = (state, event) => {
  let next = state;
  const handle = bindLifted(handleCompleteSource, {
    setProgress: (update) => {
      next = typeof update === 'function' ? update(next) : update;
    }
  });
  handle({
    serviceKey: 'depotMapping',
    operationId: '6d1f0c3e-2b7a-4f7e-9a51-0b8c2d4e6f10',
    percentComplete: 100,
    error: null,
    context: {},
    ...event
  });
  return next;
};

test('a depot scan that fails or is canceled after its bar reached the end does not advance the wizard', () => {
  const atTheEnd = polled({ status: 'running', progressPercent: 100 });
  const failed = complete(atTheEnd, {
    success: false,
    canceled: false,
    status: 'failed',
    stageKey: 'signalr.depotMapping.failed',
    error: 'signalr.depotMapping.failed'
  });
  const canceled = complete(atTheEnd, {
    success: false,
    canceled: true,
    status: 'canceled',
    stageKey: 'signalr.depotMapping.canceled'
  });
  const succeeded = complete(atTheEnd, {
    success: true,
    canceled: false,
    status: 'completed',
    stageKey: 'signalr.depotMapping.finalized'
  });

  assert.equal(advances(failed), false, 'a failed scan at 100% advances the wizard');
  assert.equal(advances(canceled), false, 'a canceled scan at 100% advances the wizard');
  assert.equal(advances(succeeded), true, 'a finished scan does not advance the wizard');
  assert.equal(
    advances(polled({ isProcessing: false, status: 'completed', progressPercent: 100 })),
    true,
    'a poll that answers the completed phase does not advance the wizard'
  );
});

test('the wizard names the connecting phase only while the poll answers connecting', () => {
  const connecting = chinese('initialization.picsProgress.connecting');
  assert.equal(
    render(polled({ status: 'connecting', progressPercent: 0, processedBatches: 0 })).includes(
      connecting
    ),
    true,
    `a scan the poll calls connecting does not show "${connecting}"`
  );
  assert.equal(
    render(polled({ status: 'idle' })).includes(connecting),
    false,
    'a scan the poll calls idle is labeled as connecting'
  );
});

test('the wizard says a scan is reconnecting to Steam in the reader language', () => {
  const html = render(
    polled({
      status: 'running',
      stageKey: 'signalr.depotMapping.reconnecting',
      context: { reconnectAttempt: 2, maxReconnectAttempts: 3 }
    })
  );
  const shown = chinese('signalr.depotMapping.reconnecting', {
    reconnectAttempt: 2,
    maxReconnectAttempts: 3
  });
  assert.equal(html.includes(shown), true, `the wizard does not show "${shown}"`);
});
