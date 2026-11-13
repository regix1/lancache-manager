import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import {
  bindLifted,
  compileTree,
  findSoleNode,
  liftConstArrow,
  moduleUrl,
  parseSource
} from './transpile-module.mjs';

/**
 * The prefill Activity Log draws each row through a `memo` component whose only clock input is the
 * `clock` prop, built from `useReaderClock`. The stamp's date form follows the app language, so a
 * language switch has to hand the rows a new `clock`, or React keeps every row already on screen in
 * the language the reader just left while new rows arrive in the new one.
 *
 * The shipped `useReaderClock` and the log's own `clock` memo are lifted from their files and run
 * on a small hook runtime that reuses a memo's value while every dependency is the same, as React
 * does. The row is redrawn only when one of its props changes, as `memo` does.
 */

const i18nUrl = moduleUrl(
  'export default { language: "en", t: (key) => key, changeLanguage() {} };'
);
const i18n = (await import(i18nUrl)).default;
const { formatTimestamp } = await import(
  await compileTree('../src/utils/dateTimeFormat.ts', { '@/i18n': i18nUrl })
);

const runtime = { slots: [], cursor: 0 };
const useMemo = (compute, deps) => {
  const index = runtime.cursor++;
  const previous = runtime.slots[index];
  if (
    previous &&
    previous.deps.length === deps.length &&
    previous.deps.every((dep, position) => Object.is(dep, deps[position]))
  ) {
    return previous.value;
  }
  const value = compute();
  runtime.slots[index] = { deps, value };
  return value;
};

// One stable preference object, as the timezone context hands out between saves.
const timezone = {
  useLocalTimezone: true,
  useUtcTimezone: true,
  use24HourFormat: false,
  refreshKey: 0
};
const useReaderClock = bindLifted(liftConstArrow('src/hooks/useReaderClock.ts', 'useReaderClock'), {
  useMemo,
  useTimezone: () => timezone,
  useTranslation: () => ({ i18n: { language: i18n.language } })
});

const logPath = 'src/components/features/prefill/ActivityLog.tsx';
const logSource = parseSource(logPath, ts.ScriptKind.TSX);
const clockMemo = findSoleNode(
  logSource,
  'Activity Log clock memo',
  (node) =>
    ts.isVariableDeclaration(node) &&
    node.name.getText(logSource) === 'clock' &&
    node.initializer !== undefined &&
    ts.isCallExpression(node.initializer) &&
    node.initializer.expression.getText(logSource) === 'useMemo'
).initializer.getText(logSource);
const useLogClock = bindLifted(`(readerClock) => ${clockMemo}`, { useMemo });

const entry = { id: 'entry-1', type: 'info', timestamp: '2026-10-10T10:00:00Z' };
const rowStamp = (clock) => formatTimestamp(entry.timestamp, { ...clock, style: 'timeSeconds' });

/** One render of the log: the hooks in source order, then the row, redrawn only on a new prop. */
const renderLog = (row) => {
  runtime.cursor = 0;
  const clock = useLogClock(useReaderClock());
  const props = { entry, isLast: true, clock };
  if (row.props && Object.keys(props).every((key) => Object.is(props[key], row.props[key]))) {
    return row;
  }
  return { props, stamp: rowStamp(clock) };
};

test('an Activity Log row already on screen re-formats its time when the app language changes', () => {
  i18n.language = 'en';
  let row = renderLog({});
  const english = row.stamp;

  i18n.language = 'zh';
  row = renderLog(row);
  const chinese = rowStamp({ ...row.props.clock });

  assert.notEqual(chinese, english, 'the two languages format this time the same way');
  assert.equal(
    row.stamp,
    chinese,
    `after switching to Chinese the row still reads "${row.stamp}"; a fresh row reads "${chinese}"`
  );
});
