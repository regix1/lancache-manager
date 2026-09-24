import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import {
  bindLifted,
  compileTree,
  findSoleNode,
  liftConstArrow,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

const timezoneUrl = await compileTree('../src/utils/timezone.ts');
const { setServerTimezone } = await import(timezoneUrl);
const { formatTimestamp } = await import(
  await compileTree('../src/utils/dateTimeFormat.ts', {
    './timezone': timezoneUrl,
    '@/i18n': moduleUrl('export default { t: key => key };')
  })
);
const { getPrefillRunProgress, isPrefillRunActive } = await import(
  await compileTree('../src/components/features/prefill/hooks/prefillTypes.ts')
);
const { clockFromTimeSetting } = await import(
  await compileTree('../src/utils/pendingPreferences.ts')
);
const ClockContext = React.createContext(clockFromTimeSetting('server-24h'));
const useReaderClock = bindLifted(liftConstArrow('src/hooks/useReaderClock.ts', 'useReaderClock'), {
  useMemo: React.useMemo,
  useTimezone: () => React.useContext(ClockContext)
});
const useFormattedDateTime = bindLifted(
  liftConstArrow('src/hooks/useFormattedDateTime.ts', 'useFormattedDateTime'),
  {
    useMemo: React.useMemo,
    useReaderClock,
    formatTimestamp
  }
);
const FormattedTimestamp = bindLifted(
  liftConstArrow('src/components/common/FormattedDateTime.tsx', 'FormattedTimestamp'),
  {
    React,
    useFormattedDateTime
  },
  { jsx: ts.JsxEmit.React }
);
const source = parseSource(
  'src/components/features/prefill/PrefillProgressCard.tsx',
  ts.ScriptKind.TSX
);
const component = findSoleNode(
  source,
  'run card',
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'PrefillProgressCard'
);
const content = ({ children }) => React.createElement(React.Fragment, null, children);
const PrefillProgressCard = bindLifted(
  component.getText(source).replace(/^export\s+/, ''),
  {
    React,
    useTranslation: () => ({ t: (key) => key }),
    useContext: React.useContext,
    useEffect: React.useEffect,
    useId: React.useId,
    useState: React.useState,
    PrefillContext: React.createContext(null),
    FormattedTimestamp,
    Card: content,
    Button: content,
    Tooltip: content,
    Alert: content,
    CollapsibleRegion: content,
    Badge: content,
    ChevronDown: () => null,
    Download: () => null,
    LoadingSpinner: () => null,
    isPrefillRunActive,
    formatBytes: String,
    formatSpeed: String,
    formatPercent: String,
    formatCount: String,
    formatTimeRemaining: String,
    formatEtaShort: String
  },
  { jsx: ts.JsxEmit.React }
);
const startedAt = '2026-09-13T23:42:13Z';
const render = (setting, state, timestamp = startedAt) => {
  const run = {
    runId: 'run',
    sessionId: 'session',
    daemonInstanceId: 'daemon',
    scheduleName: 'Daily',
    options: { selection: 'selected', appIds: ['440'], operatingSystems: ['windows'] },
    snapshot: {
      startedAt: timestamp,
      state,
      totalApps: 1,
      completedApps: 0,
      cachedApps: 0,
      failedApps: 0,
      skippedApps: 0,
      cancelledApps: state === 'cancelled' ? 1 : 0,
      bytesTransferred: 10
    },
    recovering: state === 'reconnecting',
    cancelRequested: false
  };
  const html = renderToStaticMarkup(
    React.createElement(
      ClockContext.Provider,
      {
        value: { ...clockFromTimeSetting(setting), refreshKey: 0 }
      },
      React.createElement(PrefillProgressCard, {
        run,
        progress: getPrefillRunProgress(run),
        history: true
      })
    )
  );
  return html.match(/<dt>prefill\.runs\.startedLabel<\/dt><dd>(.*?)<\/dd>/)?.[1];
};

test('every run state renders its start using the selected reader clock and time format', () => {
  setServerTimezone('Europe/Berlin');
  for (const state of ['downloading', 'reconnecting', 'completed', 'cancelled', 'failed']) {
    for (const setting of ['server-24h', 'server-12h', 'local-24h', 'local-12h', 'utc']) {
      const expected = new Date(startedAt).toLocaleString(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone:
          setting === 'utc' ? 'UTC' : setting.startsWith('server') ? 'Europe/Berlin' : undefined,
        hour12: setting.endsWith('12h')
      });
      assert.equal(render(setting, state), expected, `${state} on ${setting}`);
    }
  }
});

test('run dates cross midnight with the selected zone without changing the recorded instant', () => {
  setServerTimezone('Europe/Berlin');
  const utc = render('utc', 'cancelled');
  const server = render('server-24h', 'cancelled');
  assert.notEqual(utc, server);
  assert.equal(render('utc', 'cancelled'), utc);
  assert.equal(render('server-24h', 'cancelled'), server);
});

test('a card says reconnecting once, and the older card says a cached or finished game once', () => {
  const markup = (props) =>
    renderToStaticMarkup(
      React.createElement(
        ClockContext.Provider,
        { value: { ...clockFromTimeSetting('utc'), refreshKey: 0 } },
        React.createElement(PrefillProgressCard, props)
      )
    );
  const recovering = {
    runId: 'run',
    sessionId: 'session',
    daemonInstanceId: 'daemon',
    scheduleName: 'Daily',
    options: { selection: 'selected', appIds: ['440'], operatingSystems: ['windows'] },
    snapshot: {
      startedAt,
      state: 'running',
      totalApps: 1,
      completedApps: 0,
      cachedApps: 0,
      failedApps: 0,
      skippedApps: 0,
      cancelledApps: 0,
      bytesTransferred: 0
    },
    recovering: true,
    cancelRequested: false
  };
  const card = markup({ run: recovering, progress: getPrefillRunProgress(recovering) });
  assert.equal(card.match(/prefill\.progress\.reconnecting/g).length, 1);

  const older = (state) =>
    markup({
      onCancel: () => undefined,
      progress: {
        state,
        currentAppId: '440',
        currentAppName: 'Portal',
        percentComplete: 100,
        bytesDownloaded: 0,
        totalBytes: 0,
        bytesPerSecond: 0,
        elapsedSeconds: 0
      }
    });
  assert.doesNotMatch(older('already_cached'), /[uU]pToDate/);
  assert.equal(older('reconnecting').match(/prefill\.progress\.reconnecting/g).length, 1);
  const finished = older('app_completed');
  assert.equal(finished.match(/prefill\.progress\.loadingNextGame/g).length, 1);
  assert.doesNotMatch(finished, /prefill\.progress\.complete\b/);
});

test('missing and invalid run starts use the shared timestamp placeholders', () => {
  assert.equal(render('utc', 'cancelled', ''), 'common.notAvailable');
  assert.equal(render('utc', 'cancelled', 'invalid'), 'common.time.invalidDate');
});

test('schedule summary dates follow the selected clock while relative last-run labels do not shift', async () => {
  const summary = parseSource(
    'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillScheduleDetail.tsx',
    ts.ScriptKind.TSX
  );
  const date = findSoleNode(
    summary,
    'next run date',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(summary) === 'nextRunDate'
  );
  const ScheduleDate = bindLifted(
    `({ timestamp: nextRunUtc }) => ${date.initializer.getText(summary)}`,
    { useFormattedDateTime }
  );
  const { formatLastRun } = await import(
    await compileTree('../src/components/features/management/schedules/scheduleFormatting.ts')
  );
  const lastRun = new Date(Date.now() - 9 * 86400000).toISOString();
  setServerTimezone('Europe/Berlin');
  for (const setting of ['server-24h', 'server-12h', 'local-24h', 'local-12h', 'utc']) {
    const formatted = renderToStaticMarkup(
      React.createElement(
        ClockContext.Provider,
        {
          value: { ...clockFromTimeSetting(setting), refreshKey: 0 }
        },
        React.createElement(ScheduleDate, { timestamp: startedAt })
      )
    );
    assert.equal(formatted, render(setting, 'completed'));
    assert.equal(
      formatLastRun(lastRun, (key, values) => `${key}:${values.count}`),
      'management.schedules.daysAgo:9'
    );
  }
});
