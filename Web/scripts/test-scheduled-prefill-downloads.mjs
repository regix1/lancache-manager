import assert from 'node:assert/strict';
import test from 'node:test';
import ts from 'typescript';
import { compileToUrl, findSoleNode, parseSource, transpile } from './transpile-module.mjs';

const { isPrefillRunActive, getPrefillRunProgress, supportsConcurrentPrefill } = await import(
  await compileToUrl('../src/components/features/prefill/hooks/prefillTypes.ts')
);
const source = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillDownloads.tsx',
  ts.ScriptKind.TSX
);
const component = findSoleNode(
  source,
  'downloads component',
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'ScheduledPrefillDownloads'
);
const compiled = transpile(
  component.getText(source).replace(/^export\s+/, ''),
  ts.ModuleKind.CommonJS,
  {
    jsx: ts.JsxEmit.React,
    jsxFactory: 'h'
  }
);
const h = (type, props, ...children) => ({
  type,
  props: props ?? {},
  children: children.flat().filter(Boolean)
});
const card = Symbol('progress card');
const button = Symbol('button');
const disclosure = Symbol('disclosure');
const walk = (node, type) => [
  ...(node?.type === type ? [node] : []),
  ...(node?.children ?? []).flatMap((child) => walk(child, type))
];
const run = (id, state) => ({
  runId: id,
  sessionId: 'session',
  daemonInstanceId: 'daemon',
  scheduleId: id,
  options: { selection: 'selected', appIds: [id], operatingSystems: ['windows'] },
  snapshot: {
    state,
    startedAt: `2026-09-13T10:00:0${id}Z`,
    totalApps: 1,
    bytesTransferred: 0,
    completedApps: 0,
    cachedApps: 0,
    failedApps: 0,
    skippedApps: 0,
    cancelledApps: 0
  },
  items: [],
  recovering: false,
  cancelRequested: false
});
const create = () => {
  let open = false;
  const render = new Function(
    'useId',
    'useState',
    'useTranslation',
    'Button',
    'CollapsibleRegion',
    'ChevronDown',
    'PrefillProgressCard',
    'getPrefillRunProgress',
    'isPrefillRunActive',
    'supportsConcurrentPrefill',
    'formatBytes',
    'LoadingSpinner',
    'h',
    `${compiled}\nreturn ScheduledPrefillDownloads;`
  )(
    () => 'history',
    () => [
      open,
      (update) => {
        open = update(open);
      }
    ],
    () => ({ t: (key) => key }),
    button,
    disclosure,
    Symbol('chevron'),
    card,
    getPrefillRunProgress,
    isPrefillRunActive,
    supportsConcurrentPrefill,
    (value) => `${value} B`,
    Symbol('loading'),
    h
  );
  return render;
};

test('platform downloads separate active runs from newest-first history and cancel the exact run', () => {
  const render = create();
  const cancellations = [];
  const tree = render({
    serviceKey: 'steam',
    disabled: false,
    container: { runs: [run('1', 'cancelled'), run('2', 'downloading'), run('3', 'failed')] },
    cancellingRunIds: ['2'],
    runErrors: { 2: 'Cancellation failed' },
    onCancelDownload: (id) => cancellations.push(id)
  });
  const active = walk(tree, card).filter((node) => !node.props.history);
  const history = walk(tree, card).filter((node) => node.props.history);
  assert.deepEqual(
    active.map((node) => node.props.run.runId),
    ['2']
  );
  assert.deepEqual(
    history.map((node) => node.props.run.runId),
    ['3', '1']
  );
  assert.equal(active[0].props.isCancelling, true);
  assert.equal(active[0].props.error, 'Cancellation failed');
  active[0].props.onCancel();
  assert.deepEqual(cancellations, ['2']);
  assert.equal(
    history.every((node) => node.props.onCancel === undefined),
    true
  );
});

test('history starts collapsed and its disclosure retains the accessible open state', () => {
  const render = create();
  const props = { serviceKey: 'xbox', disabled: false, onCancelDownload: () => undefined };
  let tree = render(props);
  assert.equal(walk(tree, disclosure)[0].props.open, false);
  walk(tree, button)[0].props.onClick();
  tree = render(props);
  assert.equal(walk(tree, button)[0].props['aria-expanded'], true);
  assert.equal(walk(tree, disclosure)[0].props.open, true);
  assert.equal(walk(tree, button)[0].props['aria-controls'], 'history');
});

test('a finished run leaves active downloads and remains available in history', () => {
  const render = create();
  const props = { serviceKey: 'epic', disabled: false, onCancelDownload: () => undefined };
  let cards = walk(render({ ...props, container: { runs: [run('1', 'downloading')] } }), card);
  assert.equal(cards[0].props.history, undefined);
  cards = walk(render({ ...props, container: { runs: [run('1', 'completed')] } }), card);
  assert.equal(cards.length, 1);
  assert.equal(cards[0].props.history, true);
});

test('a legacy daemon download remains visible and cancellable without per-run snapshots', () => {
  const cancellations = [];
  const tree = create()({
    serviceKey: 'epic',
    disabled: false,
    container: { isPrefilling: true, currentAppName: 'Example game', totalBytesTransferred: 100 },
    onCancelDownload: (id) => cancellations.push(id)
  });
  const cancel = walk(tree, button).find((node) => node.props.color === 'stop');
  assert.ok(cancel);
  cancel.props.onClick();
  assert.deepEqual(cancellations, [undefined]);
  assert.doesNotMatch(JSON.stringify(tree), /prefill\.runs\.noActive/);
});
