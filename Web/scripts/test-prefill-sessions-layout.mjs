import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { findSoleNode, parseSource, transpile } from './transpile-module.mjs';

const source = parseSource(
  'src/components/features/management/sections/PrefillSessionsSection.tsx',
  ts.ScriptKind.TSX
);
const component = findSoleNode(
  source,
  'persistent container',
  (node) =>
    ts.isVariableDeclaration(node) && node.name.getText(source) === 'PersistentContainerCard'
);
const compiled = transpile(
  `const PersistentContainerCard = ${component.initializer.getText(source)};`,
  ts.ModuleKind.CommonJS,
  { jsx: ts.JsxEmit.React, jsxFactory: 'h' }
);
const runs = Symbol('runs');
const badge = Symbol('badge');
const timestamp = Symbol('timestamp');
const region = Symbol('region');
const walk = (node, type) => [
  ...(node?.type === type ? [node] : []),
  ...(node?.children ?? []).flatMap((child) => walk(child, type))
];
const render = (props) =>
  new Function(
    'useTranslation',
    'useActivityStatus',
    'useState',
    'resolveServiceId',
    'isAnonymousServiceId',
    'serviceDisplayName',
    'rowToggleHandlers',
    'formatBytes',
    'scheduleIntervalLabel',
    'StatusDot',
    'Badge',
    'Button',
    'ChevronDown',
    'CollapsibleRegion',
    'PrefillRuns',
    'FormattedTimestamp',
    'EmptyState',
    'h',
    `${compiled}\nreturn PersistentContainerCard;`
  )(
    () => ({ t: (key) => key }),
    () => ({ isActive: () => false }),
    () => [true, () => undefined],
    (service) => service,
    (service) => service === 'Riot',
    (service) => service,
    () => ({}),
    String,
    () => 'paused',
    Symbol('dot'),
    badge,
    Symbol('button'),
    Symbol('chevron'),
    region,
    runs,
    timestamp,
    Symbol('empty'),
    (type, attributes, ...children) => ({
      type,
      props: attributes ?? {},
      children: children.flat().filter(Boolean)
    })
  )(props);

test('persistent session activity and schedule counts have separate owners', () => {
  const entries = [{ runId: 'first' }, { runId: 'second' }];
  const tree = render({
    container: {
      service: 'Steam',
      sessionId: 'session',
      isRunning: true,
      activeRunCount: 1,
      runs: entries
    },
    schedules: [
      { id: 'one', name: 'Daily', enabled: true },
      { id: 'two', name: 'Weekly', enabled: false }
    ]
  });
  assert.equal(walk(tree, runs)[0].props.runs, entries);
  assert.equal(walk(tree, runs)[0].props.activeCount, 1);
  assert.equal(walk(tree, runs)[0].props.onCancel, undefined);
  const schedules = walk(tree, 'section').find(
    (node) => node.props.className === 'prefill-session-detail__schedules'
  );
  const count = walk(schedules, badge).find((node) => node.props.className === 'badge-count');
  assert.deepEqual(count.children, [2]);
  assert.equal(walk(tree, region)[0].children[0].props.inert, false);
});

test('login expiry is labelled inside details and only appears for a signed-in container', () => {
  for (const isAuthenticated of [false, true]) {
    const tree = render({
      container: {
        service: 'Steam',
        sessionId: 'session',
        isRunning: true,
        isAuthenticated,
        authExpiresAtUtc: '2026-12-12T10:00:00Z'
      },
      schedules: []
    });
    const details = walk(tree, 'dl')[0];
    assert.deepEqual(
      walk(details, 'dt').map((node) => node.children[0]),
      [
        'activeSessions.labels.sessionId',
        ...(isAuthenticated ? ['prefill.persistent.reloginRequiredBy'] : [])
      ]
    );
    assert.equal(walk(details, timestamp).length, isAuthenticated ? 1 : 0);
    assert.equal(walk(tree, timestamp).length, walk(details, timestamp).length);
  }
});

test('neutral buttons have no decorative ring and retain keyboard focus styling', () => {
  const css = readFileSync(
    new URL('../src/styles/components/buttons.css', import.meta.url),
    'utf8'
  );
  assert.match(css, /\.btn-field-surface\s*\{[^}]*box-shadow: none;/);
  assert.doesNotMatch(css, /box-shadow:\s*inset/);
  assert.match(css, /button:focus-visible,[\s\S]*?outline: 2px solid var\(--theme-border-focus\);/);
  assert.match(
    css,
    /\.btn-icon-square:focus-visible\s*\{[^}]*border-color: var\(--theme-border-focus\);/
  );
});

test('active prefill progress uses a separate track surface and a visible boundary', () => {
  const css = readFileSync(new URL('../src/styles/features/prefill.css', import.meta.url), 'utf8');
  assert.match(css, /\.prefill-run-progress\s*\{[^}]*background: var\(--theme-bg-primary\);/);
  assert.match(css, /\.prefill-run-progress\s*\{[^}]*border: 1px solid var\(--theme-text-muted\);/);
  assert.match(
    css,
    /\.prefill-run-progress::-webkit-progress-bar\s*\{[^}]*background: var\(--theme-bg-primary\);/
  );
});
