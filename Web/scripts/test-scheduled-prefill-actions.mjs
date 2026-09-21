import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

import {
  bindLifted,
  compileToUrl,
  findSoleNode,
  liftHookCallback,
  parseSource,
  transpile
} from './transpile-module.mjs';

const detailSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillScheduleDetail.tsx',
  ts.ScriptKind.TSX
);
const panelSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPlatformsPanel.tsx',
  ts.ScriptKind.TSX
);
const platformSectionSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPlatformSection.tsx',
  ts.ScriptKind.TSX
);
const configModalSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillConfigModal.tsx',
  ts.ScriptKind.TSX
);
const gameSelectionSource = parseSource(
  'src/components/features/prefill/GameSelectionModal.tsx',
  ts.ScriptKind.TSX
);
const persistentCardSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillPersistentCard.tsx',
  ts.ScriptKind.TSX
);
const containerSettingsSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillContainerSettings.tsx',
  ts.ScriptKind.TSX
);
const progressCardSource = parseSource(
  'src/components/features/prefill/PrefillProgressCard.tsx',
  ts.ScriptKind.TSX
);
const actionMenuSource = parseSource('src/components/ui/ActionMenu.tsx', ts.ScriptKind.TSX);
const modalSource = parseSource('src/components/ui/Modal.tsx', ts.ScriptKind.TSX);
const schedulesCss = readFileSync(
  new URL('../src/components/features/management/schedules/SchedulesSection.css', import.meta.url),
  'utf8'
);
const prefillCss = readFileSync(
  new URL('../src/styles/features/prefill.css', import.meta.url),
  'utf8'
);
const en = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);
const zh = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/zh.json', import.meta.url), 'utf8')
);
const focusUrl = await compileToUrl('../src/utils/focus.ts');
const { getFocusable } = await import(focusUrl);
const cachedAppsUrl = await compileToUrl('../src/components/features/prefill/cachedApps.ts');
const { resolveCacheStatus } = await import(cachedAppsUrl);
const cacheStatusUrl = await compileToUrl('../src/components/features/prefill/cacheStatus.ts');
const { completeCacheApps, groupCacheApps, markCacheAppsUnknown } = await import(cacheStatusUrl);
globalThis.HTMLInputElement = class HTMLInputElement {};

test('Run Now clears only optimistic state when no follow-up was queued, including silent responses', async () => {
  const file = process.env.SCHEDULE_ACTION_SOURCE
    ? ts.createSourceFile(
        'SchedulesSection.tsx',
        readFileSync(process.env.SCHEDULE_ACTION_SOURCE, 'utf8'),
        ts.ScriptTarget.Latest,
        true,
        ts.ScriptKind.TSX
      )
    : parseSource(
        'src/components/features/management/schedules/SchedulesSection.tsx',
        ts.ScriptKind.TSX
      );
  const declaration = findSoleNode(
    file,
    'handleRunNow callback',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(file) === 'handleRunNow' &&
      node.initializer.getText(file).includes('ApiService.triggerSchedule')
  );
  for (const showNotification of [true, false]) {
    for (const followUpQueued of [false, true, undefined]) {
      const notifications = [];
      const pending = new Set();
      let completed = { other: 'completed' };
      const run = bindLifted(declaration.initializer.arguments[0].getText(file), {
        t: (key) => key,
        recoverScheduledPrefillEditSession: async () => undefined,
        sessionStore: {},
        markStarting: (key) => pending.add(key),
        clearPending: (key) => pending.delete(key),
        setCompletedKeys: (update) => {
          completed = update(completed);
        },
        setTimeout: () => undefined,
        cacheQueuedReasonKey: 'queued',
        ApiService: {
          triggerSchedule: async () => ({
            alreadyRunning: true,
            status: 'alreadyRunning',
            showNotification,
            followUpQueued
          })
        },
        addNotification: (notification) => notifications.push(notification),
        getErrorMessage: (error) => error.message
      });
      await run('scheduledPrefill');
      assert.equal(pending.has('scheduledPrefill'), followUpQueued !== false);
      assert.equal(completed.other, 'completed');
      assert.equal(completed.scheduledPrefill, followUpQueued === false ? undefined : 'navigate');
      assert.equal(notifications.length, showNotification ? 1 : 0);
      if (showNotification)
        assert.equal(
          notifications[0].message,
          followUpQueued === false
            ? 'management.schedules.runNowAlreadyRunning'
            : 'management.schedules.runNowQueuedNext'
        );
    }
  }
});

test('Run All acknowledges active services without inventing follow-up runs', async () => {
  const source = liftHookCallback(
    'src/components/features/management/schedules/SchedulesSection.tsx',
    'useCallback',
    'ApiService.runAllSchedules()'
  );
  for (const followUpCount of [0, 1, 2, undefined]) {
    const notifications = [];
    const steps = [];
    const run = bindLifted(source, {
      setRunningAll: (value) => steps.push(['busy', value]),
      recoverScheduledPrefillEditSession: async () => undefined,
      sessionStore: {},
      setRunAllConfirmOpen: (value) => steps.push(['confirm', value]),
      ApiService: {
        runAllSchedules: async () => ({ triggeredCount: 3, alreadyRunningCount: 2, followUpCount })
      },
      fetchSchedules: async () => steps.push(['refresh']),
      flashAll: () => steps.push(['flash']),
      addNotification: (notification) => notifications.push(notification),
      t: (key, values) => `${key}:${values.count}`,
      getErrorMessage: (error) => error.message
    });
    await run();
    assert.deepEqual(steps, [
      ['busy', true],
      ['refresh'],
      ['flash'],
      ['busy', false],
      ['confirm', false]
    ]);
    assert.equal(notifications.length, followUpCount !== undefined && followUpCount < 2 ? 1 : 0);
    if (notifications.length)
      assert.equal(
        notifications[0].message,
        `management.schedules.runAllAlreadyRunning:${2 - followUpCount}`
      );
  }
});

const anchoredSource = parseSource('src/hooks/useAnchoredPanel.ts');
const { clampToViewport, MENU_GUTTER_PX } = await import(
  await compileToUrl('../src/utils/viewportClamp.ts')
);
const widthImport = actionMenuSource.statements.find(
  (node) => ts.isImportDeclaration(node) && node.moduleSpecifier.text === '@utils/dropdownWidth'
);
const widthModule = widthImport
  ? await import(await compileToUrl('../src/utils/dropdownWidth.ts'))
  : {};

function placement(options = {}, browser = {}) {
  const window = {
    innerWidth: 390,
    innerHeight: 844,
    scrollX: 0,
    scrollY: 0,
    getComputedStyle: () => ({ fontSize: '16px' }),
    ...browser
  };
  const document = {
    documentElement: { clientWidth: window.innerWidth, clientHeight: window.innerHeight }
  };
  globalThis.window = window;
  globalThis.document = document;
  const bindings = { window, document, clampToViewport };
  for (const name of ['DEFAULT_ANCHOR_GAP_PX', 'POSITION_EPSILON_PX']) {
    const declaration = findSoleNode(
      anchoredSource,
      name,
      (node) => ts.isVariableDeclaration(node) && node.name.getText(anchoredSource) === name
    );
    bindings[name] = bindLifted(`() => (${declaration.initializer.getText(anchoredSource)})`, {})();
  }
  for (const name of ['isSamePlacement', 'placeBelowAnchor']) {
    const declaration = getComponent(anchoredSource, name);
    bindings[name] = bindLifted(declaration.getText(anchoredSource), bindings);
  }
  const panelRef = { current: null };
  const published = [];
  let move;
  let stateIndex = 0;
  const hook = bindLifted(
    getComponent(anchoredSource, 'useAnchoredPanel')
      .getText(anchoredSource)
      .replace(/^export /, ''),
    {
      ...bindings,
      useCallback: (callback) => callback,
      useRef: (current) => ({ current }),
      useState: (initial) => {
        let value = initial;
        const index = stateIndex++;
        return [
          value,
          (update) => {
            value = typeof update === 'function' ? update(value) : update;
            if (index === 0) published.push(value);
          }
        ];
      },
      useExitPresence: () => ({ present: true, closing: false }),
      DROPDOWN_EXIT_MS: 150,
      useLayoutEffect: () => undefined,
      useEffect: () => undefined,
      useAnchorFollow: ({ onAnchorMove }) => {
        move = onAnchorMove;
      }
    }
  );
  hook({
    open: true,
    anchorRef: { current: null },
    panelRef,
    onClose: () => undefined,
    gutter: MENU_GUTTER_PX,
    ...options
  });
  return { panelRef, published, move, window };
}

function actionOptions(props = {}) {
  const declaration = findSoleNode(
    actionMenuSource,
    'ActionMenu declaration',
    (node) => ts.isVariableDeclaration(node) && node.name.getText(actionMenuSource) === 'ActionMenu'
  ).initializer;
  const statements = declaration.body.statements;
  const hookIndex = statements.findIndex((node) =>
    node.getText(actionMenuSource).includes('useAnchoredPanel(')
  );
  assert.ok(hookIndex >= 0);
  let options;
  bindLifted(
    `(${declaration.parameters.map((node) => node.getText(actionMenuSource)).join(',')}) => {
      ${statements
        .slice(0, hookIndex + 1)
        .map((node) => node.getText(actionMenuSource))
        .join('\n')}
    }`,
    {
      useRef: (current) => ({ current }),
      useCallback: (callback) => callback,
      ...widthModule,
      MENU_GUTTER_PX,
      useAnchoredPanel: (value) => {
        options = value;
        return { present: false, closing: false, position: {}, anchorWidth: 0 };
      }
    }
  )({ isOpen: true, onClose: () => undefined, ...props });
  const { panelRef, ...rest } = options;
  assert.equal(panelRef.current, null);
  return rest;
}

test('record Actions keeps its first horizontal placement when the panel mounts', () => {
  const f = placement(actionOptions());
  const anchor = { top: 300, bottom: 344, left: 60, right: 297, width: 237, height: 44 };
  f.move(anchor);
  assert.equal(f.published[0].left, 60);
  f.panelRef.current = { offsetWidth: 237, offsetHeight: 146 };
  f.move(anchor);
  assert.equal(f.published[1].left, 60);
  for (const position of f.published) assert.equal(position.left + 237, 297);
});

test('Actions width variants retain alignment at both viewport edges', () => {
  for (const [width, triggerWidth, measuredWidth, fontSize] of [
    [undefined, 237, 237, '16px'],
    ['w-40', 44, 160, '16px'],
    ['w-56', 44, 224, '16px'],
    ['w-[200px]', 44, 200, '16px'],
    ['w-[12rem]', 44, 192, '16px'],
    ['w-40', 44, 200, '20px'],
    ['w-40', 237.25, 237.25, '16px']
  ]) {
    for (const align of ['left', 'right']) {
      for (const left of [2, 390 - triggerWidth - 2]) {
        const f = placement(actionOptions({ width, align }), {
          getComputedStyle: () => ({ fontSize })
        });
        const anchor = {
          top: 300,
          bottom: 344,
          left,
          right: left + triggerWidth,
          width: triggerWidth,
          height: 44
        };
        f.move(anchor);
        f.panelRef.current = { offsetWidth: measuredWidth, offsetHeight: 146 };
        f.move(anchor);
        assert.equal(f.published[0].left, f.published[1].left, `${width}/${align}/${left}`);
        for (const position of f.published) {
          assert.ok(position.left >= 8);
          assert.ok(position.left + measuredWidth <= 382);
        }
      }
    }
  }
});

test('panel width estimates are optional and positive measurements take precedence', () => {
  const anchor = { top: 300, bottom: 344, left: 60, right: 297, width: 237, height: 44 };
  let calls = 0;
  const f = placement({
    align: 'right',
    initialWidth: () => {
      calls += 1;
      return 237;
    }
  });
  f.move(anchor);
  f.panelRef.current = { offsetWidth: 0, offsetHeight: 146 };
  f.move(anchor);
  assert.equal(calls, 2);
  assert.equal(f.published[0].left, 60);
  assert.equal(f.published[1].left, 60);
  f.panelRef.current.offsetWidth = 224;
  f.move(anchor);
  assert.equal(calls, 2, 'measured layout does not call the estimate');
  assert.equal(f.published[2].left, 73);

  for (const initialWidth of [
    undefined,
    ...[-1, NaN, Infinity, -Infinity, 0].map((value) => () => value)
  ]) {
    const fallback = placement({ align: 'right', initialWidth });
    fallback.move(anchor);
    assert.equal(fallback.published[0].left, 297);
  }
  const defaultPanel = placement();
  defaultPanel.move(anchor);
  assert.equal(defaultPanel.published[0].left, 60, 'default alignment stays left');
  assert.equal(defaultPanel.published[0].top, 348, 'default gap stays four pixels');
});

test('custom placement receives the same space and document offsets are added once', () => {
  const anchor = { top: 300, bottom: 344, left: 60, right: 297, width: 237, height: 44 };
  const spaces = [];
  const f = placement(
    {
      place: (space) => {
        spaces.push(space);
        return { left: 20, top: 40, openUpward: true, availableHeight: 180 };
      }
    },
    { scrollX: 31, scrollY: 3642 }
  );
  f.move(anchor);
  assert.deepEqual(spaces[0], {
    anchor,
    panelWidth: 0,
    panelHeight: 0,
    viewportWidth: 390,
    viewportHeight: 844,
    gutter: 8
  });
  assert.deepEqual(f.published[0], { left: 51, top: 3682, openUpward: true, availableHeight: 180 });
  f.panelRef.current = { offsetWidth: 100, offsetHeight: 200 };
  f.move(anchor);
  assert.deepEqual(spaces[1], { ...spaces[0], panelWidth: 100, panelHeight: 200 });
});

test('Actions follows later size and anchor changes with nonzero document scrolling', () => {
  const f = placement(actionOptions(), { scrollX: 31, scrollY: 3642 });
  const anchor = { top: 300, bottom: 344, left: 60, right: 297, width: 237, height: 44 };
  f.move(anchor);
  f.panelRef.current = { offsetWidth: 237, offsetHeight: 146 };
  f.move(anchor);
  for (const position of f.published) {
    assert.equal(position.left - f.window.scrollX, 60);
    assert.equal(position.top - f.window.scrollY, 348);
  }
  f.panelRef.current.offsetWidth = 250;
  f.move(anchor);
  assert.equal(f.published[2].left - 31, 47);
  f.move({ ...anchor, left: 80, right: 317 });
  assert.equal(f.published[3].left - 31, 67);
  f.move({ ...anchor, top: 750, bottom: 794 });
  assert.equal(f.published[4].openUpward, true);
  assert.equal(f.published[4].top - 3642, 600);
  f.panelRef.current.offsetHeight = 900;
  f.move({ ...anchor, top: 750, bottom: 794 });
  assert.equal(f.published[5].top - 3642, 8);
});

test('shared dropdown width resolution preserves tokens, units and fallbacks', () => {
  const { window } = placement();
  const { resolveDropdownWidthToPx } = widthModule;
  for (const [width, expected] of [
    [undefined, 44],
    ['', 44],
    ['w-40', 160],
    ['w-56', 224],
    ['other w-56 rounded', 224],
    ['w-[210.5px]', 210.5],
    ['w-[12rem]', 192],
    ['280px', 280],
    ['18rem', 288],
    ['50%', 195],
    ['50vw', 195],
    ['123', 123],
    ['w-auto', 44],
    ['unrecognized', 44],
    ['w-full', 358],
    ['w-screen', 358]
  ])
    assert.equal(resolveDropdownWidthToPx(width, 44), expected, String(width));
  window.getComputedStyle = () => ({ fontSize: '20px' });
  assert.equal(resolveDropdownWidthToPx('w-40', 44), 200);
  assert.equal(resolveDropdownWidthToPx('12rem', 44), 240);
  window.getComputedStyle = () => ({ fontSize: 'invalid' });
  assert.equal(resolveDropdownWidthToPx('w-40', 44), 160);
  assert.equal(resolveDropdownWidthToPx('w-full', 500), 500);
  assert.equal(resolveDropdownWidthToPx('w-[500px]', 44), 500);
});

function getComponent(source, name) {
  return findSoleNode(
    source,
    `${name} component`,
    (node) => ts.isFunctionDeclaration(node) && node.name?.text === name
  );
}

const containerSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/useScheduledPrefillContainers.ts'
);
const sharedSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillSharedSettingsModal.tsx',
  ts.ScriptKind.TSX
);
const activitySource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillActivityModal.tsx',
  ts.ScriptKind.TSX
);
const persistentLoginHostSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/PersistentLoginHost.tsx',
  ts.ScriptKind.TSX
);
const persistentPlatformSources = [
  ['SteamPersistentLogin', 'Steam', 'usePersistentSteamAuth', 'SteamAuthModal'],
  ['EpicPersistentLogin', 'Epic', 'usePersistentEpicAuth', 'EpicAuthModal'],
  ['XboxPersistentLogin', 'Xbox', 'usePersistentXboxAuth', 'XboxAuthModal']
].map(([name, service, hook, modal]) => ({
  name,
  service,
  hook,
  modal,
  source: parseSource(
    `src/components/features/management/schedules/scheduled-prefill/login/${name}.tsx`,
    ts.ScriptKind.TSX
  )
}));
const containerSignalRSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/usePersistentPrefillContainerSignalR.ts'
);
const challengeSignalRSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/usePersistentLoginChallengeSignalR.ts'
);
const containerModalSource = parseSource(
  'src/components/features/management/schedules/scheduled-prefill/ScheduledPrefillContainerModal.tsx',
  ts.ScriptKind.TSX
);
const baseKey = 'management.schedules.services.scheduledPrefill.config';
const order = ['steam', 'epic', 'xbox', 'battleNet', 'riot'];
const record = (id = '00000000-0000-4000-8000-000000000001', patch = {}) => ({
  id,
  name: 'Evening',
  enabled: true,
  intervalHours: 24,
  customSchedule: null,
  preset: 'All',
  selectedAppIds: ['10', 'not-in-library'],
  topCount: null,
  operatingSystems: ['Windows'],
  force: false,
  maxConcurrency: { mode: 'Auto' },
  notificationMode: 'all',
  notificationDisplayMode: 'full',
  ...patch
});
const configuration = (records = [record()]) => ({
  version: 6,
  maxServiceRuntime: '01:00:00',
  stallTimeout: '00:05:00',
  persistenceMode: 'keepAcrossRestart',
  ...Object.fromEntries(
    order.map((key, index) => [
      key,
      {
        serviceId: ['Steam', 'Epic', 'Xbox', 'BattleNet', 'Riot'][index],
        persistenceMode: null,
        schedules: key === 'steam' ? records : []
      }
    ])
  )
});
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
};
const flush = async () => {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
};
const arrow = (source, name, bindings) => {
  assert.ok(source.statements.length > 0);
  assert.equal(source.parseDiagnostics.length, 0);
  const declaration = findSoleNode(
    source,
    name,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(source) === name
  );
  const expression = ts.isCallExpression(declaration.initializer)
    ? declaration.initializer.arguments[0]
    : declaration.initializer;
  return bindLifted(expression.getText(source), bindings);
};
const effect = (source, token, bindings) =>
  bindLifted(liftHookCallback(source.fileName, 'useEffect', token), bindings);
const h = (type, props, ...children) =>
  typeof type === 'function'
    ? type({ ...props, children: children.flat(Infinity) })
    : {
        type,
        props: props ?? {},
        children: children.flat(Infinity).filter((child) => child != null && child !== false)
      };
const walk = (node, type) => [
  ...(node?.type === type ? [node] : []),
  ...(node?.children ?? []).flatMap((child) => walk(child, type))
];
const textOf = (node) =>
  typeof node === 'string' ? node : (node?.children ?? []).map(textOf).join('');
function component(source, name, bindings) {
  const declaration = findSoleNode(
    source,
    `${name} component`,
    (node) =>
      (ts.isFunctionDeclaration(node) && node.name?.text === name) ||
      (ts.isVariableDeclaration(node) && node.name.getText(source) === name)
  );
  const componentText = ts.isFunctionDeclaration(declaration)
    ? declaration.getText(source).replace(/^export /, '')
    : `const ${name} = ${declaration.initializer.getText(source)};`;
  const compiled = transpile(componentText, ts.ModuleKind.CommonJS, {
    jsx: ts.JsxEmit.React,
    jsxFactory: 'h',
    jsxFragmentFactory: 'Fragment'
  });
  return new Function(...Object.keys(bindings), 'h', 'Fragment', compiled + '\nreturn ' + name)(
    ...Object.values(bindings),
    h,
    'fragment'
  );
}
let actionId = 0;
const actionMenuGroup = component(actionMenuSource, 'ActionMenuGroup', {
  useId: () => `action-group-${++actionId}`
});
const actionMenuDivider = component(actionMenuSource, 'ActionMenuDivider', {});
const primitives = {
  useTranslation: () => ({ t: (key) => key }),
  Button: 'Button',
  ActionMenu: 'ActionMenu',
  ActionMenuGroup: actionMenuGroup,
  ActionMenuDivider: actionMenuDivider,
  ActionMenuItem: 'ActionMenuItem',
  ActionMenuDangerItem: 'ActionMenuDangerItem',
  LoadingSpinner: 'LoadingSpinner',
  ChevronDown: 'ChevronDown',
  StatusDot: 'StatusDot',
  useFormattedDateTime: (value) => value,
  formatLastRun: (value) => value,
  ScheduleIntervalPicker: 'ScheduleIntervalPicker',
  useState: (initial) => [initial, () => undefined],
  useRef: (current) => ({ current }),
  useId: () => `action-panel-${++actionId}`,
  SCHEDULED_PREFILL_PLATFORM_UI: { steam: { icon: 'SteamIcon', rowClassName: 'steam' } }
};

test('ActionMenu optional semantics preserve default consumers and native command buttons', () => {
  const render = component(actionMenuSource, 'ActionMenu', {
    useRef: (current) => ({ current }),
    useCallback: (callback) => callback,
    useEffect: () => undefined,
    useAnchoredPanel: () => ({
      present: true,
      closing: false,
      position: { top: 20, left: 30 },
      anchorWidth: 40
    }),
    resolveDropdownWidthToPx: () => 160,
    getFocusable: () => [],
    MENU_GUTTER_PX,
    createPortal: (node) => node,
    document: { body: {} }
  });
  const draw = (props = {}) =>
    render({
      isOpen: true,
      onClose: () => undefined,
      trigger: h('button', { type: 'button' }, 'Open'),
      children: h('button', { type: 'button' }, 'Command'),
      ...props
    });
  const panel = (tree) =>
    walk(tree, 'div').find((node) => node.props.className?.includes('am-dropdown'));

  const ordinary = panel(draw());
  assert.equal(ordinary.props.id, undefined);
  assert.equal(ordinary.props.role, undefined);
  assert.equal(ordinary.props['aria-label'], undefined);

  const named = panel(draw({ id: 'scheduled-actions', 'aria-label': 'Actions' }));
  assert.equal(named.props.id, 'scheduled-actions');
  assert.equal(named.props.role, 'group');
  assert.equal(named.props['aria-label'], 'Actions');
  assert.ok(
    [...walk(named, 'div'), ...walk(named, 'button')].every(
      (node) => node.props.role !== 'menu' && node.props.role !== 'menuitem'
    )
  );

  const item = component(
    actionMenuSource,
    'ActionMenuItem',
    {}
  )({
    onClick: () => undefined,
    children: 'Command'
  });
  const danger = component(
    actionMenuSource,
    'ActionMenuDangerItem',
    {}
  )({
    onClick: () => undefined,
    children: 'Delete'
  });
  assert.equal(item.type, 'button');
  assert.equal(item.props.role, undefined);
  assert.equal(danger.type, 'button');
  assert.equal(danger.props.role, undefined);

  const defaultDivider = actionMenuDivider({});
  const semanticDivider = actionMenuDivider({ semantic: true });
  assert.equal(defaultDivider.props.role, undefined);
  assert.equal(defaultDivider.props['aria-orientation'], undefined);
  assert.equal(semanticDivider.props.role, 'separator');
  assert.equal(semanticDivider.props['aria-orientation'], 'horizontal');
});

test('all selected source modules parse nonempty production code', () => {
  for (const source of [
    detailSource,
    panelSource,
    platformSectionSource,
    configModalSource,
    gameSelectionSource,
    persistentCardSource,
    containerSettingsSource,
    progressCardSource,
    actionMenuSource,
    containerSource,
    sharedSource,
    activitySource,
    containerSignalRSource,
    challengeSignalRSource,
    containerModalSource
  ]) {
    assert.ok(source.statements.length > 0, source.fileName);
    assert.equal(source.parseDiagnostics.length, 0, source.fileName);
  }
  assert.match(schedulesCss, /overscroll-behavior: contain/);
  assert.match(prefillCss, /prefill/);
});

test('selection login and discard copy stays aligned across English and Chinese', () => {
  const config = (locale) => locale.management.schedules.services.scheduledPrefill.config;
  const scheduled = (locale) => config(locale).selectedGames;
  const serviceKeys = ['steam', 'epic', 'xbox', 'battleNet', 'riot'];

  assert.deepEqual(
    {
      run: config(en).records.run,
      manageContainer: config(en).records.manageContainer,
      menuAdd: config(en).records.menuAdd,
      addForService: config(en).records.addForService
    },
    {
      run: 'Run schedule',
      manageContainer: 'Manage container',
      menuAdd: 'Add {{service}} schedule',
      addForService: 'Add schedule for {{service}}'
    }
  );
  assert.deepEqual(
    {
      run: config(zh).records.run,
      manageContainer: config(zh).records.manageContainer,
      menuAdd: config(zh).records.menuAdd,
      addForService: config(zh).records.addForService
    },
    {
      run: '运行计划',
      manageContainer: '管理容器',
      menuAdd: '添加 {{service}} 计划',
      addForService: '为 {{service}} 添加计划'
    }
  );
  assert.deepEqual(
    serviceKeys.map((serviceKey) =>
      config(en).records.menuAdd.replace('{{service}}', config(en).services[serviceKey])
    ),
    [
      'Add Steam schedule',
      'Add Epic schedule',
      'Add Xbox schedule',
      'Add Battle.net schedule',
      'Add Riot schedule'
    ]
  );
  assert.deepEqual(
    serviceKeys.map((serviceKey) =>
      config(zh).records.menuAdd.replace('{{service}}', config(zh).services[serviceKey])
    ),
    [
      '添加 Steam 计划',
      '添加 Epic 计划',
      '添加 Xbox 计划',
      '添加 Battle.net 计划',
      '添加 Riot 计划'
    ]
  );
  assert.equal(
    scheduled(en).signInToSelectGames,
    'Sign in to select games. Use {{actions}} → {{manageContainer}}.'
  );
  assert.equal(
    scheduled(zh).signInToSelectGames,
    '请先登录再选择游戏。前往“{{actions}}”→“{{manageContainer}}”登录。'
  );
  assert.equal(
    scheduled(en)
      .signInToSelectGames.replace('{{actions}}', en.management.actions.menuLabel)
      .replace('{{manageContainer}}', config(en).records.manageContainer),
    'Sign in to select games. Use Actions → Manage container.'
  );
  assert.equal(
    scheduled(zh)
      .signInToSelectGames.replace('{{actions}}', zh.management.actions.menuLabel)
      .replace('{{manageContainer}}', config(zh).records.manageContainer),
    '请先登录再选择游戏。前往“操作”→“管理容器”登录。'
  );
  assert.deepEqual(
    {
      modalDescription: config(en).modalDescription,
      containerModalDescription: config(en).containerModalDescription,
      activityDescription: config(en).activityDescription,
      deleteBody: config(en).records.deleteBody,
      downloadNow: config(en).persistentContainer.downloadNow,
      requiresPersistentContainer: scheduled(en).requiresPersistentContainer,
      workflow: config(en).persistentContainers.workflow,
      runNow: en.management.schedules.runNow
    },
    {
      modalDescription: 'Edit this schedule, then save it. Run the saved schedule from its row.',
      containerModalDescription: 'This container is shared by all {{service}} schedules.',
      activityDescription: 'Active downloads and run history for every service.',
      deleteBody: 'Deletes this saved schedule. It does not stop or delete the service container.',
      downloadNow: 'Run this schedule',
      requiresPersistentContainer:
        'Save or close this schedule, then start its container from Manage the service container.',
      workflow: {
        stopped:
          'Start the shared service container, then sign in to choose games for this schedule.',
        stoppedAnonymous: 'Start the shared service container to choose games for this schedule.',
        needsLogin: 'Sign in to the shared service container to choose games for this schedule.',
        selectGames: 'Choose games for this schedule, then save it.',
        ready: 'The shared service container is ready. Save this schedule before running it.'
      },
      runNow: 'Run Now'
    }
  );
  assert.deepEqual(
    {
      modalDescription: config(zh).modalDescription,
      containerModalDescription: config(zh).containerModalDescription,
      activityDescription: config(zh).activityDescription,
      deleteBody: config(zh).records.deleteBody,
      downloadNow: config(zh).persistentContainer.downloadNow,
      requiresPersistentContainer: scheduled(zh).requiresPersistentContainer,
      workflow: config(zh).persistentContainers.workflow,
      runNow: zh.management.schedules.runNow
    },
    {
      modalDescription: '编辑并保存此计划，然后从对应行运行已保存的计划。',
      containerModalDescription: '此容器由 {{service}} 的所有计划共享。',
      activityDescription: '查看所有服务的当前下载和运行历史。',
      deleteBody: '删除此已保存的计划，不会停止或删除该服务的容器。',
      downloadNow: '运行此计划',
      requiresPersistentContainer: '保存或关闭此计划，再通过“管理此服务的容器”启动容器。',
      workflow: {
        stopped: '启动共享服务容器并登录，再为此计划选择游戏。',
        stoppedAnonymous: '启动共享服务容器，再为此计划选择游戏。',
        needsLogin: '登录共享服务容器，再为此计划选择游戏。',
        selectGames: '为此计划选择游戏，然后保存。',
        ready: '共享服务容器已就绪。请先保存此计划，再运行它。'
      },
      runNow: '立即运行'
    }
  );
  for (const locale of [en, zh]) {
    assert.deepEqual(
      [...scheduled(locale).signInToSelectGames.matchAll(/\{\{([^}]+)\}\}/g)].map(
        (match) => match[1]
      ),
      ['actions', 'manageContainer']
    );
    assert.equal('requiresLogin' in scheduled(locale), false);
    assert.deepEqual(Object.keys(locale.prefill.gameSelection.discardChanges), [
      'confirmTitle',
      'confirmBody',
      'confirmButton'
    ]);
  }
  assert.equal(configModalSource.text.includes('selectedGames.requiresLogin'), false);
  assert.equal([...detailSource.text.matchAll(/records\.menuAdd/g)].length, 1);
  assert.equal([...detailSource.text.matchAll(/records\.addForService/g)].length, 1);
});

function createGamePicker(overrides = {}) {
  const runtime = hooks();
  const closed = [];
  const saved = [];
  const notices = [];
  const render = component(gameSelectionSource, 'GameSelectionModal', {
    ...runtime,
    useTranslation: () => ({ t: (key) => key }),
    useErrorHandler: () => ({ notifyError: (...args) => notices.push(args) }),
    noAutofill: {},
    Modal: 'Modal',
    Button: 'Button',
    Tooltip: 'Tooltip',
    Badge: 'Badge',
    Alert: 'Alert',
    CollapsibleRegion: 'CollapsibleRegion',
    CustomScrollbar: 'CustomScrollbar',
    SearchInput: 'SearchInput',
    Check: 'Check',
    Gamepad2: 'Gamepad2',
    Import: 'Import',
    Database: 'Database',
    LoadingSpinner: 'LoadingSpinner',
    ConfirmationModal: 'ConfirmationModal',
    EmptyState: 'EmptyState',
    resolveCacheStatus
  });
  const props = {
    opened: true,
    onClose: () => closed.push(true),
    serviceId: 'steam',
    games: [
      { appId: '1', name: 'One' },
      { appId: '2', name: 'Two' }
    ],
    selectedAppIds: ['1'],
    onSave: async (selectedIds) => saved.push(selectedIds),
    confirmDiscard: true,
    ...overrides
  };

  runtime.render(render, props);
  let tree = runtime.render(render, props);
  const draw = () => {
    tree = runtime.render(render, props);
    return tree;
  };
  const button = (label) =>
    walk(tree, 'Button')
      .filter((node) => textOf(node) === label)
      .at(-1);
  return {
    runtime,
    props,
    closed,
    saved,
    notices,
    draw,
    tree: () => tree,
    button,
    row: (appId) => walk(tree, 'Button').find((node) => node.props['data-game-app-id'] === appId),
    modal: () => walk(tree, 'Modal')[0],
    confirmation: () => walk(tree, 'ConfirmationModal').find((node) => node.props.opened === true)
  };
}

test('dirty scheduled picker dismissal keeps choices until discard is confirmed', () => {
  const picker = createGamePicker();
  picker.row('2').props.onClick();
  picker.draw();
  picker.button('common.cancel').props.onClick();
  picker.draw();

  assert.deepEqual(picker.closed, []);
  assert.ok(picker.confirmation());
});

test('clean and reverted scheduled picker dismissals close without a discard decision', () => {
  const clean = createGamePicker();
  clean.modal().props.onClose();
  clean.draw();
  assert.deepEqual(clean.closed, [true]);
  assert.equal(clean.confirmation(), undefined);

  const reverted = createGamePicker();
  reverted.row('2').props.onClick();
  reverted.draw();
  reverted.row('2').props.onClick();
  reverted.draw();
  reverted.modal().props.onClose();
  reverted.draw();
  assert.deepEqual(reverted.closed, [true]);
  assert.equal(reverted.confirmation(), undefined);
});

test('discard decline retains the picker draft and confirmation closes only the picker', () => {
  const picker = createGamePicker();
  picker.row('2').props.onClick();
  picker.draw();
  picker.button('common.cancel').props.onClick();
  picker.draw();
  picker.confirmation().props.onClose();
  picker.draw();
  assert.equal(picker.row('2').props['aria-pressed'], true);
  assert.deepEqual(picker.closed, []);

  picker.modal().props.onClose();
  picker.draw();
  picker.confirmation().props.onConfirm();
  picker.draw();
  assert.deepEqual(picker.closed, [true]);
  assert.deepEqual(picker.saved, []);
});

test('saving the scheduled picker bypasses discard while pending and failed saves stay guarded', async () => {
  const saved = createGamePicker();
  saved.row('2').props.onClick();
  saved.draw();
  await saved.button('prefill.gameSelection.saveSelection').props.onClick();
  saved.draw();
  assert.deepEqual(saved.saved, [['1', '2']]);
  assert.deepEqual(saved.closed, [true]);
  assert.equal(saved.confirmation(), undefined);

  const pendingSave = deferred();
  const pending = createGamePicker({ onSave: () => pendingSave.promise });
  pending.row('2').props.onClick();
  pending.draw();
  const saving = pending.button('prefill.gameSelection.saveSelection').props.onClick();
  pending.draw();
  assert.equal(pending.button('common.cancel').props.disabled, true);
  pending.modal().props.onClose();
  pending.draw();
  assert.deepEqual(pending.closed, []);
  pendingSave.resolve();
  await saving;
  pending.draw();
  assert.deepEqual(pending.closed, [true]);

  const failed = createGamePicker({
    onSave: async () => {
      throw new Error('save failed');
    }
  });
  failed.row('2').props.onClick();
  failed.draw();
  await failed.button('prefill.gameSelection.saveSelection').props.onClick();
  failed.draw();
  assert.equal(failed.notices.length, 1);
  failed.button('common.cancel').props.onClick();
  failed.draw();
  assert.ok(failed.confirmation());
});

test('selection membership alone owns scheduled picker dirtiness', () => {
  const clear = createGamePicker();
  clear.button('common.clear').props.onClick();
  clear.draw();
  clear.modal().props.onClose();
  clear.draw();
  assert.ok(clear.confirmation());

  const all = createGamePicker();
  all.button('common.selectAll').props.onClick();
  all.draw();
  all.modal().props.onClose();
  all.draw();
  assert.ok(all.confirmation());

  const imported = createGamePicker();
  imported.button('prefill.gameSelection.importAppIds').props.onClick();
  imported.draw();
  walk(imported.tree(), 'textarea')[0].props.onChange({ target: { value: '2' } });
  imported.draw();
  imported.button('prefill.gameSelection.import').props.onClick();
  imported.draw();
  imported.modal().props.onClose();
  imported.draw();
  assert.ok(imported.confirmation());

  const swapped = createGamePicker();
  swapped.row('1').props.onClick();
  swapped.draw();
  swapped.row('2').props.onClick();
  swapped.draw();
  swapped.props.selectedAppIds = ['1', '1'];
  swapped.props.games = [...swapped.props.games].reverse();
  swapped.draw();
  assert.equal(swapped.row('1').props['aria-pressed'], false);
  assert.equal(swapped.row('2').props['aria-pressed'], true);
  swapped.modal().props.onClose();
  swapped.draw();
  assert.ok(swapped.confirmation());

  const unknown = createGamePicker({ games: [], selectedAppIds: ['unknown'] });
  unknown.modal().props.onClose();
  unknown.draw();
  assert.deepEqual(unknown.closed, [true]);
  assert.equal(unknown.confirmation(), undefined);
});

test('scheduled picker closure resets its discard owner for the next opening', () => {
  const picker = createGamePicker();
  picker.row('2').props.onClick();
  picker.draw();
  picker.modal().props.onClose();
  picker.draw();
  assert.ok(picker.confirmation());

  picker.props.opened = false;
  picker.draw();
  assert.equal(picker.confirmation(), undefined);
  picker.props.selectedAppIds = ['2'];
  picker.props.opened = true;
  picker.draw();
  picker.draw();
  picker.modal().props.onClose();
  picker.draw();
  assert.deepEqual(picker.closed, [true]);
  assert.equal(picker.confirmation(), undefined);
});

test('ordinary picker policy keeps direct dismissal and its independent cache confirmation', () => {
  for (const confirmDiscard of [undefined, false]) {
    const picker = createGamePicker({ confirmDiscard });
    picker.row('2').props.onClick();
    picker.draw();
    picker.button('common.cancel').props.onClick();
    picker.draw();
    assert.deepEqual(picker.closed, [true]);
    assert.equal(picker.confirmation(), undefined);
  }

  const clearCalls = [];
  const picker = createGamePicker({
    confirmDiscard: false,
    cachedAppIds: ['1'],
    onClearAllCache: async () => clearCalls.push(true)
  });
  picker.button('prefill.gameSelection.clearAllCached').props.onClick();
  picker.draw();
  const confirmation = picker.confirmation();
  assert.equal(confirmation.props.title, 'prefill.confirm.clearDbTitle');
  confirmation.props.onConfirm();
  assert.deepEqual(clearCalls, [true]);
});

test('row menu exposes grouped labels and semantics without changing command callbacks', () => {
  for (const enabled of [true, false]) {
    for (const disabled of [true, false]) {
      const calls = [];
      const props = {
        serviceKey: 'steam',
        serviceId: 'Steam',
        scheduleId: record().id,
        label: 'Steam · Evening',
        enabled,
        disabled,
        enablePending: false,
        containerRunning: false,
        loginState: 'loginRequired',
        intervalHours: 24,
        customSchedule: null,
        nextTiming: '',
        nextRunUtc: null,
        lastRunUtc: null,
        isRunning: false,
        runDisabled: false,
        runPending: false,
        triggerRef: () => undefined,
        ...Object.fromEntries(
          [
            'onRun',
            'onOpen',
            'onContainer',
            'onAdd',
            'onDuplicate',
            'onDelete',
            'onToggleEnabled'
          ].map((name) => [name, (...args) => calls.push([name, ...args])])
        ),
        onIntervalChange: () => undefined,
        onCustomScheduleChange: () => undefined
      };
      const render = component(detailSource, 'ScheduledPrefillServiceScheduleRow', primitives);
      const tree = render(props);
      const menu = walk(tree, 'ActionMenu')[0];
      const trigger = menu.props.trigger;
      const groups = menu.children.filter((node) => node.props.role === 'group');
      const dividers = menu.children.filter((node) => node.props.role === 'separator');
      const savedLabel = groups[0].children[0];
      const serviceLabel = groups[1].children[0];
      const savedItems = groups[0].children.slice(1);
      const serviceItems = groups[1].children.slice(1);
      const danger = menu.children.at(-1);

      assert.equal(menu.props.id, trigger.props['aria-controls']);
      assert.equal(menu.props['aria-label'], 'management.actions.menuLabel');
      assert.equal(trigger.props['aria-expanded'], false);
      assert.equal(trigger.props['aria-haspopup'], undefined);
      assert.equal(groups.length, 2);
      assert.equal(dividers.length, 2);
      assert.ok(dividers.every((divider) => divider.props['aria-orientation'] === 'horizontal'));
      assert.equal(savedLabel.props.id, groups[0].props['aria-labelledby']);
      assert.equal(serviceLabel.props.id, groups[1].props['aria-labelledby']);
      assert.equal(textOf(savedLabel), `${baseKey}.records.label`);
      assert.equal(textOf(serviceLabel), `${baseKey}.services.steam`);
      assert.deepEqual(
        savedItems.map(textOf),
        [
          'records.edit',
          'records.run',
          'records.' + (enabled ? 'disable' : 'enable'),
          'records.duplicate'
        ].map((key) => baseKey + '.' + key)
      );
      assert.deepEqual(serviceItems.map(textOf), [
        `${baseKey}.records.menuAdd`,
        `${baseKey}.records.manageContainer`
      ]);
      assert.equal(textOf(danger), `${baseKey}.records.delete`);
      assert.ok(
        [...savedItems, ...serviceItems, danger].every((item) => item.props.role === undefined)
      );

      trigger.props.ref({ focus: () => calls.push(['focus']) });
      const commands = [...savedItems, ...serviceItems, danger];
      if (disabled) {
        assert.ok(commands.every((item) => item.props.disabled));
        commands.forEach((item) => item.props.onClick());
        assert.deepEqual(calls, []);
        continue;
      }

      assert.equal(savedItems[1].props.disabled, !enabled);
      commands.filter((item) => !item.props.disabled).forEach((item) => item.props.onClick());
      const expected = [
        ['onOpen', 'steam', record().id],
        ...(enabled ? [['onRun', 'Steam', record().id]] : []),
        ['onToggleEnabled', 'steam', record().id],
        ['onDuplicate', 'steam', record().id],
        ['onAdd', 'steam'],
        ['onContainer', 'steam'],
        ['onDelete', 'steam', record().id]
      ].flatMap((call) => [['focus'], call]);
      assert.deepEqual(calls, expected);
    }
  }
});

test('focused composition contains only record controls and disables leaves for an off record', () => {
  const section = component(platformSectionSource, 'ScheduledPrefillPlatformSection', {
    useTranslation: primitives.useTranslation,
    useRef: primitives.useRef,
    Button: 'Button',
    Badge: 'Badge',
    Tooltip: 'Tooltip',
    ScheduledPrefillDownloadFields: 'DownloadFields',
    ScheduledPrefillNotificationFields: 'NotificationFields',
    ScheduledPrefillScheduleFields: 'ScheduleFields'
  });
  const panel = component(panelSource, 'ScheduledPrefillPlatformsPanel', {
    useTranslation: primitives.useTranslation,
    FormField: ({ children }) => children[0]({ id: 'name' }),
    TextInput: 'TextInput',
    ToggleSwitch: 'ToggleSwitch',
    noAutofill: {},
    ScheduledPrefillPlatformSection: section
  });
  const changes = [];
  const tree = panel({
    serviceKey: 'steam',
    config: record(undefined, { enabled: false }),
    disabled: false,
    gameSelectionLoading: false,
    onChange: (value) => changes.push(value),
    onSelectGames: () => undefined,
    onClearGames: () => undefined
  });
  assert.equal(walk(tree, 'TextInput')[0].props.disabled, false);
  assert.equal(walk(tree, 'ToggleSwitch')[0].props.disabled, false);
  for (const tag of ['DownloadFields', 'NotificationFields', 'ScheduleFields'])
    assert.equal(walk(tree, tag)[0].props.disabled, true);
  assert.equal(walk(tree, 'ActionMenu').length, 0);
  assert.equal(walk(tree, 'PersistentCard').length, 0);
  assert.equal(walk(tree, 'Downloads').length, 0);
  walk(tree, 'ToggleSwitch')[0].props.onChange('true');
  assert.equal(changes[0].enabled, true);
});

function modalSession() {
  const reads = [];
  const state = {};
  const refs = {
    baseline: { current: null },
    dirty: { current: false },
    current: { current: null },
    identityRef: { current: 'owner' },
    gameSelectionRef: { current: null },
    gameRequestRef: { current: null },
    confirmed: { current: { onSaved: () => undefined, onLoaded: () => undefined } },
    writes: { current: new Map() }
  };
  const setters = Object.fromEntries(
    [
      'Config',
      'Loading',
      'Saving',
      'Error',
      'LoadError',
      'Missing',
      'DiscardConfirmOpen',
      'OverwriteEnabledConfirmOpen',
      'GameSelection',
      'LoadingGameSelectionService',
      'GameLoaded',
      'GameLoadError'
    ].map((name) => [
      'set' + name,
      (value) => {
        state[name] = typeof value === 'function' ? value(state[name]) : value;
      }
    ])
  );
  const bindings = {
    ...refs,
    ...setters,
    baseKey,
    t: (key) => key,
    isAbortError: (error) => error.name === 'AbortError',
    getErrorMessage: (error) => error.message,
    ApiService: {
      getScheduledPrefillConfig: () => {
        const read = deferred();
        reads.push(read);
        return read.promise;
      }
    }
  };
  return {
    state,
    refs,
    reads,
    bindings,
    open(target) {
      refs.current.current = target;
      return effect(configModalSource, 'const opening = target?.opening', {
        ...bindings,
        target
      })();
    }
  };
}
test('A to B to A late reads cannot replace the active draft or its loading state', async () => {
  const f = modalSession();
  const a = {
    opening: 1,
    serviceKey: 'steam',
    scheduleId: record().id,
    create: false,
    schedule: record()
  };
  const b = {
    ...a,
    opening: 2,
    scheduleId: record('00000000-0000-4000-8000-000000000002').id,
    schedule: record('00000000-0000-4000-8000-000000000002', { name: 'B' })
  };
  const closeA = f.open(a);
  closeA();
  const closeB = f.open(b);
  closeB();
  f.open({ ...a, opening: 3 });
  f.refs.dirty.current = true;
  f.state.Config = record(undefined, { name: 'Typed' });
  f.reads[1].reject(new Error('late'));
  f.reads[0].resolve(configuration([record(undefined, { name: 'Old' })]));
  await flush();
  assert.equal(f.state.Config.name, 'Typed');
  assert.equal(f.state.Error, null);
  assert.equal(f.state.Loading, true);
  f.reads[2].resolve(configuration([record(undefined, { name: 'Server' })]));
  await flush();
  await flush();
  assert.equal(f.state.Config.name, 'Typed');
  assert.equal(f.state.Loading, false);
});
test('missing exact ID never opens the first record and cancelled reopen restores the confirmed seed', async () => {
  const f = modalSession();
  f.open({
    opening: 1,
    serviceKey: 'steam',
    scheduleId: '00000000-0000-4000-8000-000000000099',
    create: false,
    schedule: null
  });
  f.reads[0].resolve(configuration());
  await flush();
  assert.equal(f.state.Missing, true);
  assert.equal(f.state.Config, null);
  f.open({
    opening: 2,
    serviceKey: 'steam',
    scheduleId: record().id,
    create: true,
    schedule: record()
  });
  assert.equal(f.state.Config.name, 'Evening');
  assert.equal(f.state.Loading, false);
});

test('editor read failure survives edits and a pending same-record reopen until success', async () => {
  const f = modalSession();
  const first = {
    opening: 1,
    serviceKey: 'steam',
    scheduleId: record().id,
    create: false,
    schedule: record()
  };
  f.open(first);
  f.reads[0].reject(new Error('read failed'));
  await flush();
  assert.equal(
    f.state.LoadError.message,
    'management.schedules.services.scheduledPrefill.config.summaryError'
  );

  arrow(configModalSource, 'change', {
    baseline: f.refs.baseline,
    dirty: f.refs.dirty,
    setConfig: f.bindings.setConfig,
    setError: f.bindings.setError
  })({ ...record(), name: 'Draft' });
  assert.equal(
    f.state.LoadError.message,
    'management.schedules.services.scheduledPrefill.config.summaryError'
  );

  const second = { ...first, opening: 2 };
  f.open(second);
  assert.equal(
    f.state.LoadError.message,
    'management.schedules.services.scheduledPrefill.config.summaryError'
  );
  f.reads[1].resolve(configuration());
  await flush();
  assert.equal(f.state.LoadError, null);
});
test('save confirms only its record and an old completion cannot close a later opening', async () => {
  const f = modalSession();
  const target = {
    opening: 1,
    serviceKey: 'steam',
    scheduleId: record().id,
    create: false,
    schedule: record()
  };
  f.refs.current.current = target;
  const write = deferred(),
    calls = [];
  f.refs.confirmed.current.onSaved = (...args) => calls.push(args);
  const commit = arrow(configModalSource, 'commitSave', {
    ...f.bindings,
    target,
    config: record(undefined, { name: 'Saved' }),
    saving: false,
    missing: false,
    getPersistentServiceId: () => 'Steam',
    onClose: () => calls.push('close'),
    ApiService: { updateScheduledPrefillSchedule: () => write.promise }
  });
  const saving = commit();
  f.refs.current.current = { ...target, opening: 2 };
  write.resolve(configuration([record(undefined, { name: 'Saved' })]));
  await saving;
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], 'steam');
  assert.equal(calls[0][1].name, 'Saved');
});
test('a conflicting record save retains the draft and keeps the editor open', async () => {
  const f = modalSession();
  const target = {
    opening: 1,
    serviceKey: 'steam',
    scheduleId: record().id,
    create: true,
    schedule: record()
  };
  f.refs.current.current = target;
  f.state.Config = target.schedule;
  let closed = false;
  await arrow(configModalSource, 'commitSave', {
    ...f.bindings,
    target,
    config: target.schedule,
    saving: false,
    missing: false,
    getPersistentServiceId: () => 'Steam',
    onClose: () => {
      closed = true;
    },
    ApiService: {
      createScheduledPrefillSchedule: async () => {
        throw new Error('name conflict');
      }
    }
  })();
  assert.equal(closed, false);
  assert.equal(f.state.Config, target.schedule);
  assert.equal(f.state.Saving, false);
  assert.ok(f.state.Error);
});
test('summary coalesces one read and one trailing pass and rejects a read started before a mutation', async () => {
  const reads = [],
    snapshots = [],
    request = { current: null },
    revision = { current: 0 };
  const refresh = arrow(detailSource, 'refreshSchedule', {
    request,
    revision,
    ApiService: {
      getScheduledPrefillSchedule: () => {
        const read = deferred();
        reads.push(read);
        return read.promise;
      }
    },
    setSchedule: (value) => snapshots.push(value),
    setLoading: () => undefined,
    setError: () => undefined,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message
  });
  const first = refresh();
  refresh();
  refresh();
  assert.equal(reads.length, 1);
  revision.current++;
  reads[0].resolve([{ name: 'Old' }]);
  await flush();
  assert.equal(reads.length, 2);
  assert.equal(snapshots.length, 0);
  reads[1].resolve([{ name: 'Confirmed' }]);
  await first;
  assert.deepEqual(snapshots, [[{ name: 'Confirmed' }]]);
});
test('every schedules event refreshes even when aggregate timing did not change', () => {
  const callbacks = new Map();
  let count = 0;
  effect(detailSource, "on('SchedulesUpdated'", {
    on: (key, callback) => callbacks.set(key, callback),
    off: () => undefined,
    refreshSchedule: () => {
      count++;
      return Promise.resolve();
    }
  })();
  callbacks.get('SchedulesUpdated')([]);
  callbacks.get('SchedulesUpdated')([]);
  assert.equal(count, 2);
});
test('partial settings save retains only the failed dirty field for retry', async () => {
  let dayWrites = 0,
    modeWrites = 0,
    closes = 0;
  const dirty = { current: { days: true, mode: true } },
    saved = { current: { days: 30, mode: 'keepAcrossRestart' } };
  const revisions = { current: { days: 0, mode: 0 } };
  let errors = {};
  const bindings = {
    saving: false,
    session: { current: { opened: true, id: 1 } },
    days: 60,
    mode: 'fullPersistence',
    dirty,
    saved,
    revisions,
    setSaving: () => undefined,
    setMode: () => undefined,
    setReadErrors: () => undefined,
    setSaveErrors: (update) => {
      errors = update(errors);
    },
    onClose: () => closes++,
    getErrorMessage: (error) => error.message,
    containers: { loadPersistentContainers: () => undefined },
    ApiService: {
      updatePersistentPrefillValidity: async () => {
        dayWrites++;
      },
      setScheduledPrefillPersistence: async () => {
        modeWrites++;
        if (modeWrites === 1) throw new Error('mode failed');
        return { persistenceMode: 'fullPersistence' };
      }
    }
  };
  await arrow(sharedSource, 'save', bindings)();
  assert.deepEqual(dirty.current, { days: false, mode: true });
  assert.equal(closes, 0);
  assert.equal(errors.mode, 'mode failed');
  await arrow(sharedSource, 'save', bindings)();
  assert.equal(dayWrites, 1);
  assert.equal(modeWrites, 2);
  assert.equal(closes, 1);
});
test('unread shared settings never write the other field', async () => {
  let writes = 0;
  await arrow(sharedSource, 'save', {
    saving: false,
    session: { current: { opened: true, id: 1 } },
    days: 60,
    mode: null,
    dirty: { current: { days: false, mode: true } },
    saved: { current: { days: 60, mode: null } },
    revisions: { current: { days: 0, mode: 0 } },
    setSaving: () => undefined,
    setMode: () => undefined,
    setReadErrors: () => undefined,
    setSaveErrors: () => undefined,
    onClose: () => undefined,
    getErrorMessage: (error) => error.message,
    containers: { loadPersistentContainers: () => undefined },
    ApiService: {
      setScheduledPrefillPersistence: async () => {
        writes++;
      }
    }
  })();
  assert.equal(writes, 0);
});
test('Activity renders five stable service sections and sends cancel to the exact service', () => {
  const calls = [];
  const render = component(activitySource, 'ScheduledPrefillActivityModal', {
    useTranslation: primitives.useTranslation,
    Modal: 'Modal',
    Button: 'Button',
    Alert: 'Alert',
    CustomScrollbar: 'Scrollbar',
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    supportsConcurrentPrefill: (container) => container.maxConcurrentRuns > 1,
    ScheduledPrefillDownloads: 'Downloads'
  });
  const tree = render({
    opened: true,
    disabled: false,
    onClose: () => undefined,
    containers: {
      persistentError: 'refresh failed',
      containersByServiceKey: new Map(),
      cancellingRunIds: [],
      runErrors: {},
      handleCancelPersistentDownload: (...args) => calls.push(args)
    }
  });
  const downloads = walk(tree, 'Downloads');
  assert.equal(walk(tree, 'Alert').length, 1);
  assert.deepEqual(
    downloads.map((node) => node.props.serviceKey),
    order
  );
  downloads[2].props.onCancelDownload('run-xbox');
  assert.deepEqual(calls, [['xbox', 'run-xbox']]);
});

test('game selection stays local and retains IDs absent from the current library', async () => {
  const picker = findSoleNode(
    configModalSource,
    'picker',
    (node) =>
      ts.isJsxSelfClosingElement(node) &&
      node.tagName.getText(configModalSource) === 'GameSelectionModal'
  );
  const save = picker.attributes.properties.find(
    (node) => ts.isJsxAttribute(node) && node.name.text === 'onSave'
  );
  assert.ok(save?.initializer?.expression);
  let changed;
  const config = record();
  const callback = bindLifted(save.initializer.expression.getText(configModalSource), {
    config,
    target: { scheduleId: config.id },
    gameSelection: {
      scheduleId: config.id,
      games: [
        { appId: '10', name: 'Known' },
        { appId: '20', name: 'New' }
      ]
    },
    change: (value) => {
      changed = value;
    }
  });
  await callback(['20']);
  assert.deepEqual(changed.selectedAppIds, ['not-in-library', '20']);
  assert.deepEqual(config.selectedAppIds, ['10', 'not-in-library']);
});

test('selection opening refuses account login gaps without side effects and keeps anonymous services ready', () => {
  const openSelection = (serviceKey, gameSelectionNeedsLogin) => {
    const changes = [];
    const gameSelectionRef = { current: null };
    arrow(configModalSource, 'handleOpenGameSelection', {
      target: {
        opening: 1,
        serviceKey,
        scheduleId: record().id,
        create: false,
        schedule: record()
      },
      config: record(),
      saving: false,
      gameSelectionNeedsLogin,
      container: {
        sessionId: 'session',
        isRunning: true,
        isAuthenticated: !gameSelectionNeedsLogin,
        needsRelogin: false
      },
      baseKey,
      t: (key) => key,
      isScheduledPrefillAnonymousService: (key) => ['battleNet', 'riot'].includes(key),
      setError: (value) => changes.push(['error', value]),
      setGameLoadError: (value) => changes.push(['loadError', value]),
      setGameLoaded: (value) => changes.push(['loaded', value]),
      gameRequestRef: { current: null },
      gameSelectionRef,
      setGameSelection: (value) => changes.push(['selection', value]),
      loadGameSelection: (...args) => changes.push(['load', ...args])
    })();
    return { changes, gameSelectionRef };
  };

  for (const serviceKey of ['steam', 'epic', 'xbox']) {
    const blocked = openSelection(serviceKey, true);
    assert.deepEqual(blocked.changes, []);
    assert.equal(blocked.gameSelectionRef.current, null);
  }
  for (const serviceKey of ['battleNet', 'riot']) {
    const ready = openSelection(serviceKey, false);
    assert.equal(ready.gameSelectionRef.current.serviceKey, serviceKey);
    assert.deepEqual(ready.changes.at(-1), ['load', serviceKey, 'session']);
  }
});
test('shared reads succeed independently and cannot overwrite a dirty field', async () => {
  const reads = { days: deferred(), mode: deferred() },
    state = {};
  const setters = Object.fromEntries(
    [
      'Days',
      'Mode',
      'ReadErrors',
      'SaveErrors',
      'Saving',
      'Clearing',
      'ClearOpen',
      'DiscardOpen',
      'ClearOutcome',
      'Overrides'
    ].map((name) => [
      'set' + name,
      (value) => {
        state[name] = typeof value === 'function' ? value(state[name]) : value;
      }
    ])
  );
  const dirty = { current: { days: false, mode: false } };
  const revisions = { current: { days: 0, mode: 0 } };
  effect(sharedSource, 'ApiService.getPersistentPrefillValidity', {
    ...setters,
    opened: true,
    session: { current: { id: 1, opened: true } },
    dirty,
    revisions,
    saved: { current: { days: 30, mode: null } },
    t: (key) => key,
    baseKey,
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    ApiService: {
      getPersistentPrefillValidity: () => reads.days.promise,
      getScheduledPrefillConfig: () => reads.mode.promise
    }
  })();
  state.Days = 60;
  dirty.current.days = true;
  revisions.current.days += 1;
  reads.days.resolve({ days: 90 });
  reads.mode.resolve(configuration());
  await flush();
  assert.equal(state.Days, 60);
  assert.equal(state.Mode, 'keepAcrossRestart');
});
test('shared successful writes cannot be replaced by their delayed opening reads', async () => {
  const reads = { days: deferred(), mode: deferred() };
  const state = {};
  const dirty = { current: { days: false, mode: false } };
  const saved = { current: { days: 30, mode: 'keepAcrossRestart' } };
  const revisions = { current: { days: 0, mode: 0 } };
  const session = { current: { id: 1, opened: true } };
  const set = (name) => (value) => {
    state[name] = typeof value === 'function' ? value(state[name]) : value;
  };
  const common = {
    opened: true,
    session,
    dirty,
    revisions,
    saved,
    setDays: set('Days'),
    setMode: set('Mode'),
    setReadErrors: set('ReadErrors'),
    setSaveErrors: set('SaveErrors'),
    setSaving: set('Saving'),
    setClearing: set('Clearing'),
    setClearOpen: set('ClearOpen'),
    setDiscardOpen: set('DiscardOpen'),
    setClearOutcome: set('ClearOutcome'),
    setOverrides: set('Overrides'),
    t: (key) => key,
    baseKey,
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    onClose: () => undefined,
    containers: { loadPersistentContainers: () => undefined },
    ApiService: {
      getPersistentPrefillValidity: () => reads.days.promise,
      getScheduledPrefillConfig: () => reads.mode.promise,
      updatePersistentPrefillValidity: async () => undefined,
      setScheduledPrefillPersistence: async () => ({ persistenceMode: 'fullPersistence' })
    }
  };
  effect(sharedSource, 'ApiService.getPersistentPrefillValidity', common)();
  revisions.current.days += 1;
  revisions.current.mode += 1;
  dirty.current = { days: true, mode: true };
  state.Days = 60;
  state.Mode = 'fullPersistence';

  await arrow(sharedSource, 'save', {
    ...common,
    saving: false,
    days: 60,
    mode: 'fullPersistence'
  })();
  assert.deepEqual(saved.current, { days: 60, mode: 'fullPersistence' });
  assert.deepEqual(dirty.current, { days: false, mode: false });

  reads.days.resolve({ days: 90 });
  reads.mode.resolve(configuration());
  await flush();
  assert.deepEqual(saved.current, { days: 60, mode: 'fullPersistence' });
  assert.equal(state.Days, 60);
  assert.equal(state.Mode, 'fullPersistence');
});
test('summary failure retains its populated rows and drains a queued refresh', async () => {
  const reads = [],
    snapshots = [[{ name: 'Existing' }]];
  const refresh = arrow(detailSource, 'refreshSchedule', {
    request: { current: null },
    revision: { current: 0 },
    ApiService: {
      getScheduledPrefillSchedule: () => {
        const read = deferred();
        reads.push(read);
        return read.promise;
      }
    },
    setSchedule: (value) => snapshots.push(value),
    setLoading: () => undefined,
    setError: () => undefined,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message
  });
  const pending = refresh();
  refresh();
  reads[0].reject(new Error('offline'));
  await flush();
  assert.equal(reads.length, 2);
  assert.deepEqual(snapshots, [[{ name: 'Existing' }]]);
  reads[1].resolve([{ name: 'New' }]);
  await pending;
  assert.deepEqual(snapshots[1], [{ name: 'New' }]);
});

test('bulk confirmation changes only returned record enablement and preserves newer rows', async () => {
  const first = record(undefined, { enabled: false });
  const second = record('00000000-0000-4000-8000-000000000002', { enabled: false });
  let rows = [first, second].map((item) => ({ ...item, serviceId: 'Steam', scheduleId: item.id }));
  const records = { current: new Map([['steam:' + first.id, first]]) };
  const calls = [];
  await arrow(detailSource, 'bulk', {
    pending: { current: new Set() },
    disabled: false,
    setPendingKeys: () => undefined,
    ApiService: {
      updateScheduledPrefillConfig: () => assert.fail('legacy full-config write'),
      setScheduledPrefillSchedulesEnabled: async (...args) => {
        calls.push(args);
        return configuration([record()]);
      }
    },
    revision: { current: 0 },
    records,
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY: { Steam: 'steam' },
    setSchedule: (update) => {
      rows = update(rows);
    },
    setConfig: () => undefined,
    refreshSchedule: () => undefined,
    setError: assert.fail,
    getErrorMessage: (error) => error.message
  })(true);
  assert.deepEqual(
    rows.map((row) => row.enabled),
    [true, false]
  );
  assert.equal(records.current.get('steam:' + first.id).enabled, true);
  assert.deepEqual(calls, [[true]]);
  assert.deepEqual(rows[1], { ...second, serviceId: 'Steam', scheduleId: second.id });
});

function hooks() {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const changed = (previous, next) =>
    !previous ||
    !next ||
    previous.length !== next.length ||
    next.some((value, index) => value !== previous[index]);
  const runtime = {
    useState(initial) {
      const index = cursor++;
      if (!slots[index])
        slots[index] = { value: typeof initial === 'function' ? initial() : initial };
      return [
        slots[index].value,
        (value) => {
          slots[index].value = typeof value === 'function' ? value(slots[index].value) : value;
        }
      ];
    },
    useRef(initial) {
      const index = cursor++;
      if (!slots[index]) slots[index] = { current: initial };
      return slots[index];
    },
    useMemo(make, dependencies) {
      const index = cursor++;
      if (changed(slots[index]?.dependencies, dependencies))
        slots[index] = { value: make(), dependencies };
      return slots[index].value;
    },
    useCallback(callback, dependencies) {
      return runtime.useMemo(() => callback, dependencies);
    },
    useId() {
      const index = cursor++;
      if (!slots[index]) slots[index] = { value: `test-id-${index}` };
      return slots[index].value;
    },
    useEffect(callback, dependencies) {
      const index = cursor++;
      if (changed(slots[index]?.dependencies, dependencies)) {
        effects.push(() => {
          slots[index]?.cleanup?.();
          slots[index] = { dependencies, cleanup: callback() };
        });
      }
    },
    flushEffects() {
      const pending = effects;
      effects = [];
      pending.forEach((effect) => effect());
    },
    render(render, props, commit, flushPending = true) {
      cursor = 0;
      const tree = render(props);
      commit?.(tree);
      if (flushPending) runtime.flushEffects();
      return tree;
    },
    unmount() {
      slots.forEach((slot) => slot?.cleanup?.());
    }
  };
  return runtime;
}

class ModalNode {
  constructor(document, name) {
    this.document = document;
    this.name = name;
    this.connected = true;
    this.parent = null;
    this.children = [];
    this.focusables = [];
    this.focusCalls = 0;
  }

  append(child) {
    child.parent = this;
    this.children.push(child);
    child.setConnected(this.connected);
  }

  contains(target) {
    for (let node = target; node; node = node.parent) if (node === this) return true;
    return false;
  }

  focus() {
    this.focusCalls++;
    this.document.activeElement = this;
  }

  setConnected(connected) {
    this.connected = connected;
    for (const child of this.children) child.setConnected(connected);
  }
}

function createModalDocument(activeElement) {
  const root = {
    setAttribute: () => undefined,
    removeAttribute: () => undefined
  };
  return {
    activeElement,
    body: {},
    documentElement: { classList: { add: () => undefined, remove: () => undefined } },
    getElementById: () => root,
    contains: (node) => node.connected
  };
}

function createModalHarness(document, onClose = () => undefined) {
  const runtime = hooks();
  const refs = [];
  const timers = new Map();
  const contentNode = new ModalNode(document, 'dialog');
  let timerId = 0;
  let tree = null;
  const React = {
    ...runtime,
    useRef(initial) {
      const ref = runtime.useRef(initial);
      if (!refs.includes(ref)) refs.push(ref);
      return ref;
    }
  };
  const render = component(modalSource, 'Modal', {
    React,
    useTranslation: () => ({ t: (key) => key }),
    createPortal: (node) => node,
    X: 'X',
    getFocusable: (node) => node.focusables,
    document,
    Node: ModalNode,
    modalStack: [],
    modalIdCounter: 0,
    MODAL_BASE_Z: 80,
    MODAL_ELEVATED_BASE_Z: 10080,
    modalTopZ: 80,
    elevatedTopZ: 10080,
    setTimeout: (callback, delay) => {
      const id = ++timerId;
      timers.set(id, { callback, delay });
      return id;
    },
    clearTimeout: (id) => timers.delete(id)
  });
  const commit = (nextTree) => {
    const previousDialog = walk(tree, 'div').find((node) => node.props.role === 'dialog');
    const nextDialog = walk(nextTree, 'div').find((node) => node.props.role === 'dialog');
    if (previousDialog && !nextDialog) {
      previousDialog.props.ref.current = null;
      contentNode.setConnected(false);
    }
    if (nextDialog) {
      contentNode.setConnected(true);
      nextDialog.props.ref.current = contentNode;
    }
    tree = nextTree;
  };
  return {
    contentNode,
    draw(opened, flushPending = true) {
      return runtime.render(
        render,
        { opened, onClose, children: 'dialog body' },
        commit,
        flushPending
      );
    },
    open() {
      this.draw(true);
      return this.draw(true);
    },
    run(delay) {
      for (const [id, timer] of [...timers]) {
        if (timer.delay !== delay) continue;
        timers.delete(id);
        timer.callback();
      }
    },
    flushEffects: () => runtime.flushEffects(),
    previousFocus: () => refs[4],
    pendingTimers: (delay) => [...timers.values()].filter((timer) => timer.delay === delay).length,
    get tree() {
      return tree;
    }
  };
}

test('shared Modal backdrop closes only for direct outer and centering-surface clicks', () => {
  const pageTrigger = new ModalNode(null, 'page trigger');
  const document = createModalDocument(pageTrigger);
  pageTrigger.document = document;
  let closes = 0;
  const modal = createModalHarness(document, () => closes++);
  const tree = modal.open();
  const outer = walk(tree, 'div').find((node) => node.props.className.includes('modal-backdrop'));
  const center = outer.children[0];
  const card = walk(tree, 'div').find((node) => node.props.role === 'dialog');
  const nestedTarget = new ModalNode(document, 'nested dialog');

  outer.props.onClick({ target: outer, currentTarget: outer });
  assert.equal(closes, 1, 'outer padding closes once');

  center.props.onClick({ target: center, currentTarget: center });
  outer.props.onClick({ target: center, currentTarget: outer });
  assert.equal(closes, 2, 'centering gutter and its bubbled outer event close once');

  center.props.onClick({ target: card, currentTarget: center });
  outer.props.onClick({ target: card, currentTarget: outer });
  assert.equal(closes, 2, 'dialog descendants stay open');

  center.props.onClick({ target: nestedTarget, currentTarget: center });
  outer.props.onClick({ target: nestedTarget, currentTarget: outer });
  assert.equal(closes, 2, 'nested portal clicks do not close an ancestor');
});

test('shared Modal restores focus after committed removal and rejects detached openers', () => {
  for (const connected of [true, false]) {
    const trigger = new ModalNode(null, connected ? 'connected trigger' : 'detached trigger');
    const document = createModalDocument(trigger);
    trigger.document = document;
    const modal = createModalHarness(document);
    modal.open();
    assert.equal(document.activeElement, modal.contentNode, 'opening retains entrance focus');

    modal.draw(false);
    modal.run(250);
    assert.ok(modal.tree, 'the timer requests hiding before the committed removal');
    assert.equal(trigger.focusCalls, 0, 'the timer does not restore focus before removal');
    trigger.setConnected(connected);
    modal.draw(false);

    assert.equal(trigger.focusCalls, connected ? 1 : 0);
    assert.equal(modal.previousFocus().current, null, 'the stored opener clears after close');
  }
});

test('nested Modal focus restoration ends on the page trigger for every close ordering', () => {
  for (const order of ['batched', 'confirmation-first', 'editor-first']) {
    const pageTrigger = new ModalNode(null, 'Actions');
    const document = createModalDocument(pageTrigger);
    pageTrigger.document = document;
    const editor = createModalHarness(document);
    editor.open();
    const editorTrigger = new ModalNode(document, 'Cancel');
    editor.contentNode.append(editorTrigger);
    document.activeElement = editorTrigger;
    const confirmation = createModalHarness(document);
    confirmation.open();

    editor.draw(false);
    confirmation.draw(false);
    if (order === 'batched') {
      editor.run(250);
      confirmation.run(250);
      editor.draw(false, false);
      confirmation.draw(false, false);
      editor.flushEffects();
      confirmation.flushEffects();
    } else if (order === 'confirmation-first') {
      confirmation.run(250);
      confirmation.draw(false);
      assert.equal(document.activeElement, editorTrigger);
      editor.run(250);
      editor.draw(false);
    } else {
      editor.run(250);
      editor.draw(false);
      confirmation.run(250);
      confirmation.draw(false);
    }

    assert.equal(document.activeElement, pageTrigger, order);
    assert.equal(pageTrigger.focusCalls, 1, order);
    assert.equal(editorTrigger.focusCalls, order === 'confirmation-first' ? 1 : 0, order);
  }
});

test('nested Modal owns Escape and Tab and restores its editor trigger on close', () => {
  const pageTrigger = new ModalNode(null, 'Actions');
  const document = createModalDocument(pageTrigger);
  pageTrigger.document = document;
  let editorCloses = 0;
  let confirmationCloses = 0;
  const editor = createModalHarness(document, () => editorCloses++);
  editor.open();
  const editorTrigger = new ModalNode(document, 'Cancel');
  editor.contentNode.append(editorTrigger);
  document.activeElement = editorTrigger;
  const confirmation = createModalHarness(document, () => confirmationCloses++);
  const confirmationTree = confirmation.open();
  const confirmationOuter = walk(confirmationTree, 'div').find((node) =>
    node.props.className.includes('modal-backdrop')
  );
  const editorOuter = walk(editor.tree, 'div').find((node) =>
    node.props.className.includes('modal-backdrop')
  );
  let stopped = 0;
  const escape = {
    key: 'Escape',
    target: confirmation.contentNode,
    stopPropagation: () => stopped++
  };
  confirmationOuter.props.onKeyDown(escape);
  editorOuter.props.onKeyDown(escape);
  assert.equal(stopped, 1);
  assert.equal(confirmationCloses, 1);
  assert.equal(editorCloses, 0);

  const first = new ModalNode(document, 'first');
  const last = new ModalNode(document, 'last');
  confirmation.contentNode.append(first);
  confirmation.contentNode.append(last);
  confirmation.contentNode.focusables = [first, last];
  document.activeElement = first;
  let prevented = 0;
  const reverseTab = {
    key: 'Tab',
    shiftKey: true,
    target: first,
    preventDefault: () => prevented++,
    stopPropagation: () => undefined
  };
  confirmationOuter.props.onKeyDown(reverseTab);
  editorOuter.props.onKeyDown(reverseTab);
  assert.equal(document.activeElement, last);
  const forwardTab = {
    ...reverseTab,
    shiftKey: false,
    target: last
  };
  confirmationOuter.props.onKeyDown(forwardTab);
  editorOuter.props.onKeyDown(forwardTab);
  assert.equal(document.activeElement, first);
  assert.equal(prevented, 2);

  confirmation.draw(false);
  confirmation.run(250);
  confirmation.draw(false);
  assert.equal(document.activeElement, editorTrigger);
  assert.ok(walk(editor.tree, 'div').some((node) => node.props.role === 'dialog'));
});

test('stable Select Games wrapper restores focus across picker and confirmation dialogs', () => {
  const pageTrigger = new ModalNode(null, 'Actions');
  const document = createModalDocument(pageTrigger);
  pageTrigger.document = document;
  const editor = createModalHarness(document);
  editor.open();

  const selectGamesRef = { current: null };
  const opened = [];
  const section = component(platformSectionSource, 'ScheduledPrefillPlatformSection', {
    useTranslation: primitives.useTranslation,
    useRef: () => selectGamesRef,
    Button: 'Button',
    Badge: 'Badge',
    Tooltip: 'Tooltip',
    ScheduledPrefillDownloadFields: 'DownloadFields',
    ScheduledPrefillNotificationFields: 'NotificationFields',
    ScheduledPrefillScheduleFields: 'ScheduleFields'
  });
  const sectionTree = section({
    serviceKey: 'steam',
    config: record(),
    disabled: false,
    gameSelectionLoading: false,
    gameSelectionNeedsLogin: false,
    onChange: () => undefined,
    onSelectGames: () => opened.push(true),
    onClearGames: () => undefined
  });
  const stableTrigger = walk(sectionTree, 'span').find((node) =>
    node.props.className?.includes('scheduled-prefill-record-games__select-trigger')
  );
  const triggerNode = new ModalNode(document, 'Select games');
  const focusOptions = [];
  triggerNode.focus = (options) => {
    triggerNode.focusCalls++;
    focusOptions.push(options);
    document.activeElement = triggerNode;
  };
  editor.contentNode.append(triggerNode);
  stableTrigger.props.ref.current = triggerNode;
  assert.equal(stableTrigger.props.tabIndex, -1);
  walk(sectionTree, 'Button')[0].props.onClick();
  assert.deepEqual(opened, [true]);
  assert.deepEqual(focusOptions, [{ preventScroll: true }]);

  const picker = createModalHarness(document);
  picker.open();
  const pickerTrigger = new ModalNode(document, 'Cancel');
  picker.contentNode.append(pickerTrigger);
  document.activeElement = pickerTrigger;
  let confirmationCloses = 0;
  const confirmation = createModalHarness(document, () => confirmationCloses++);
  const confirmationTree = confirmation.open();
  const confirmationOuter = walk(confirmationTree, 'div').find((node) =>
    node.props.className.includes('modal-backdrop')
  );
  let stopped = 0;
  confirmationOuter.props.onKeyDown({
    key: 'Escape',
    target: confirmation.contentNode,
    stopPropagation: () => stopped++
  });
  assert.equal(stopped, 1);
  assert.equal(confirmationCloses, 1);
  confirmation.draw(false);
  confirmation.run(250);
  confirmation.draw(false);
  assert.equal(document.activeElement, pickerTrigger);
  assert.ok(walk(picker.tree, 'div').some((node) => node.props.role === 'dialog'));

  picker.draw(false);
  picker.run(250);
  picker.draw(false);
  assert.equal(document.activeElement, triggerNode);
  assert.equal(triggerNode.connected, true);

  editor.draw(false);
  editor.run(250);
  editor.draw(false);
  assert.equal(document.activeElement, pageTrigger);
});

test('shared Modal leaves initial focus alone and cancels stale close work on reopen', () => {
  const initialTrigger = new ModalNode(null, 'initial trigger');
  const initialDocument = createModalDocument(initialTrigger);
  initialTrigger.document = initialDocument;
  const initiallyClosed = createModalHarness(initialDocument);
  initiallyClosed.draw(false);
  initiallyClosed.run(250);
  initiallyClosed.draw(false);
  assert.equal(initialTrigger.focusCalls, 0);

  const pageTrigger = new ModalNode(null, 'page trigger');
  const document = createModalDocument(pageTrigger);
  pageTrigger.document = document;
  const modal = createModalHarness(document);
  modal.open();
  assert.equal(document.activeElement, modal.contentNode);
  modal.draw(false);
  assert.equal(modal.pendingTimers(250), 1);
  modal.draw(true);
  assert.equal(modal.pendingTimers(250), 0, 'reopen cancels the stale close timer');
  modal.run(250);
  modal.draw(true);
  assert.ok(walk(modal.tree, 'div').some((node) => node.props.role === 'dialog'));
  assert.equal(pageTrigger.focusCalls, 0);

  modal.draw(false);
  modal.run(250);
  modal.draw(true);
  assert.equal(pageTrigger.focusCalls, 0, 'an opened modal does not run post-close restoration');
  modal.draw(true);
  assert.equal(document.activeElement, modal.contentNode);
});

test('record enabled and timing mutations use exact narrow methods and preserve sibling records', async () => {
  const sibling = record('00000000-0000-4000-8000-000000000002', { name: 'Sibling' });
  for (const patch of [{ enabled: false }, { intervalHours: 12, customSchedule: null }]) {
    const root = configuration([record(), sibling]);
    const snapshot = structuredClone(root);
    const calls = [],
      confirmed = [];
    await arrow(detailSource, 'saveServiceConfig', {
      pending: { current: new Set() },
      disabled: false,
      schedule: root.steam.schedules.map((item) => ({
        ...item,
        scheduleId: item.id,
        serviceId: 'Steam'
      })),
      getPersistentServiceId: () => 'Steam',
      setPendingKeys: () => undefined,
      setPatches: () => undefined,
      setError: (error) => assert.equal(error, null),
      getErrorMessage: (error) => error.message,
      confirmRecord: (...args) => confirmed.push(args),
      ApiService: {
        updateScheduledPrefillConfig: () => assert.fail('legacy full-config write'),
        setScheduledPrefillScheduleEnabled: async (...args) => {
          calls.push(['enabled', ...args]);
          return configuration([{ ...record(), ...patch }, sibling]);
        },
        setScheduledPrefillScheduleTiming: async (...args) => {
          calls.push(['timing', ...args]);
          return configuration([{ ...record(), ...patch }, sibling]);
        }
      }
    })('steam', record().id, patch);
    assert.deepEqual(
      calls,
      patch.enabled !== undefined
        ? [['enabled', 'Steam', record().id, false]]
        : [['timing', 'Steam', record().id, 12, null]]
    );
    assert.deepEqual(confirmed, [['steam', { ...record(), ...patch }, true]]);
    assert.deepEqual(root, snapshot);
  }
});

test('editor and shared persistence commits never write the full configuration', async () => {
  const calls = [];
  const sibling = record('00000000-0000-4000-8000-000000000002', { name: 'Sibling' });
  const root = configuration([record(), sibling]);
  const snapshot = structuredClone(root);
  const f = modalSession();
  const target = { opening: 1, serviceKey: 'steam', scheduleId: record().id, create: false };
  f.refs.current.current = target;
  await arrow(configModalSource, 'commitSave', {
    ...f.bindings,
    target,
    config: record(),
    saving: false,
    missing: false,
    getPersistentServiceId: () => 'Steam',
    onClose: () => undefined,
    ApiService: {
      updateScheduledPrefillConfig: () => assert.fail('legacy full-config write'),
      updateScheduledPrefillSchedule: async (...args) => {
        calls.push(['record', ...args]);
        return root;
      }
    }
  })();
  await arrow(sharedSource, 'save', {
    saving: false,
    session: { current: { opened: true, id: 1 } },
    days: null,
    mode: 'fullPersistence',
    dirty: { current: { days: false, mode: true } },
    saved: { current: { days: null, mode: 'keepAcrossRestart' } },
    revisions: { current: { days: 0, mode: 0 } },
    setSaving: () => undefined,
    setMode: () => undefined,
    setReadErrors: () => undefined,
    setSaveErrors: () => undefined,
    onClose: () => undefined,
    getErrorMessage: (error) => error.message,
    containers: { loadPersistentContainers: () => undefined },
    ApiService: {
      updateScheduledPrefillConfig: () => assert.fail('legacy full-config write'),
      updatePersistentPrefillValidity: () => assert.fail('unrelated validity write'),
      setScheduledPrefillPersistence: async (...args) => {
        calls.push(['mode', ...args]);
        return { ...root, persistenceMode: 'fullPersistence' };
      }
    }
  })();
  assert.deepEqual(calls, [
    ['record', 'Steam', record()],
    ['mode', 'fullPersistence']
  ]);
  assert.deepEqual(root, snapshot);
});

test('outer editor composes record controls and selection save and clear remain local', async () => {
  const runtime = hooks();
  const t = (key) => key;
  const section = component(platformSectionSource, 'ScheduledPrefillPlatformSection', {
    useTranslation: () => ({ t }),
    useRef: runtime.useRef,
    Button: 'Button',
    Badge: 'Badge',
    Tooltip: 'Tooltip',
    ScheduledPrefillDownloadFields: 'DownloadFields',
    ScheduledPrefillNotificationFields: 'NotificationFields',
    ScheduledPrefillScheduleFields: 'ScheduleFields'
  });
  const panel = component(panelSource, 'ScheduledPrefillPlatformsPanel', {
    useTranslation: () => ({ t }),
    FormField: ({ children }) => children[0]({ id: 'name' }),
    TextInput: 'TextInput',
    ToggleSwitch: 'ToggleSwitch',
    noAutofill: {},
    ScheduledPrefillPlatformSection: section
  });
  const calls = [];
  const ApiService = new Proxy(
    {
      getPersistentPrefillGames: async (...args) => {
        calls.push(args);
        return {
          games: [
            { appId: '10', name: 'Known' },
            { appId: '20', name: 'New' }
          ],
          cachedAppIds: ['10'],
          outdatedAppIds: [],
          unknownAppIds: []
        };
      }
    },
    { get: (target, key) => target[key] ?? (() => assert.fail('forbidden editor API: ' + key)) }
  );
  const render = component(configModalSource, 'ScheduledPrefillConfigModal', {
    ...runtime,
    useTranslation: () => ({ t }),
    useScrollAreaHeight: () => [() => undefined, 400],
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    Modal: 'Modal',
    Button: 'Button',
    Alert: 'Alert',
    CustomScrollbar: 'Scrollbar',
    ConfirmationModal: 'ConfirmationModal',
    LoadingSpinner: 'LoadingSpinner',
    GameSelectionModal: 'GameSelectionModal',
    ScheduledPrefillPlatformsPanel: panel,
    ApiService,
    isAbortError: () => false,
    ApiError: Error,
    getErrorMessage: (error) => error.message,
    getPersistentServiceId: () => 'Steam',
    isScheduledPrefillAnonymousService: () => false,
    resolveCachedAppIds: (_previous, current) => current,
    completeCacheApps,
    groupCacheApps,
    markCacheAppsUnknown,
    validateServiceConfig: () => null
  });
  const props = {
    target: {
      opening: 1,
      serviceKey: 'steam',
      scheduleId: record().id,
      name: 'Evening',
      create: true,
      schedule: record()
    },
    container: {
      sessionId: 'session',
      isRunning: true,
      isAuthenticated: true,
      needsRelogin: false
    },
    identity: 'owner',
    onClose: () => undefined,
    onSaved: () => undefined,
    onLoaded: () => undefined
  };
  runtime.render(render, props);
  let tree = runtime.render(render, props);
  assert.equal(walk(tree, 'DownloadFields').length, 1);
  assert.equal(walk(tree, 'ScheduleFields').length, 1);
  assert.equal(walk(tree, 'NotificationFields').length, 1);
  for (const forbidden of [
    'ActionMenu',
    'PersistentCard',
    'Downloads',
    'ActivityModal',
    'ContainerSettings'
  ])
    assert.equal(walk(tree, forbidden).length, 0);
  const labels = walk(tree, 'Button').map(textOf);
  assert.deepEqual(labels, [
    baseKey + '.actions.selectGames',
    baseKey + '.actions.clearGames',
    'common.cancel',
    baseKey + '.actions.save'
  ]);
  const selectGamesTrigger = walk(tree, 'span').find((node) =>
    node.props.className?.includes('scheduled-prefill-record-games__select-trigger')
  );
  const focusCalls = [];
  const focusOwner = {
    focus: (options) => focusCalls.push(options)
  };
  selectGamesTrigger.props.ref.current = focusOwner;
  assert.equal(selectGamesTrigger.props.tabIndex, -1);
  assert.equal(walk(tree, 'Button')[0].props.disabled, false);
  walk(tree, 'Button')[0].props.onClick();
  assert.deepEqual(focusCalls, [{ preventScroll: true }]);
  await flush();
  tree = runtime.render(render, props);
  assert.equal(walk(tree, 'GameSelectionModal')[0].props.opened, true);
  await walk(tree, 'GameSelectionModal')[0].props.onSave(['20']);
  tree = runtime.render(render, props);
  assert.deepEqual(walk(tree, 'GameSelectionModal')[0].props.selectedAppIds, [
    'not-in-library',
    '20'
  ]);
  props.container = { ...props.container, isAuthenticated: false };
  runtime.render(render, props);
  tree = runtime.render(render, props);
  const retained = walk(tree, 'GameSelectionModal')[0].props;
  const loginTrigger = walk(tree, 'span').find((node) =>
    node.props.className?.includes('scheduled-prefill-record-games__select-trigger')
  );
  assert.equal(retained.opened, true);
  assert.equal(retained.games.length, 2);
  assert.deepEqual(retained.unknownAppIds, ['10']);
  assert.deepEqual(retained.selectedAppIds, ['not-in-library', '20']);
  assert.equal(calls.length, 1);
  assert.equal(loginTrigger.props.ref, selectGamesTrigger.props.ref);
  assert.equal(loginTrigger.props.ref.current, focusOwner);
  assert.equal(loginTrigger.props.tabIndex, 0);
  assert.ok(loginTrigger.props['aria-describedby']);
  assert.equal(walk(tree, 'Button')[0].props.disabled, true);
  props.container = { ...props.container, isAuthenticated: true };
  runtime.render(render, props);
  await flush();
  tree = runtime.render(render, props);
  assert.equal(walk(tree, 'GameSelectionModal')[0].props.opened, true);
  assert.equal(calls.length, 2);
  runtime.render(render, props);
  assert.equal(calls.length, 2);
  walk(tree, 'Button')[1].props.onClick();
  tree = runtime.render(render, props);
  assert.deepEqual(walk(tree, 'GameSelectionModal')[0].props.selectedAppIds, []);
  assert.equal(calls.length, 2);
  runtime.unmount();
});

test('retained login callback refuses a changed account before any mutation or cleanup', async () => {
  const attempted = [];
  const act = arrow(containerSource, 'act', {
    privateAvailabilityIdentity: 'account-a',
    privateAvailabilityIdentityRef: { current: 'account-b' },
    attempts: { current: new Map() },
    setActions: () => attempted.push('actions'),
    setErrors: () => attempted.push('errors'),
    recover: async () => attempted.push('cleanup')
  });
  await arrow(containerSource, 'handlePersistentLogin', { act })('epic', true);
  assert.deepEqual(attempted, []);
});

test('passive Manage leaves cached persistent login state untouched', async () => {
  const reconciled = [];
  const targets = [];
  const login = { current: null };
  const run = bindLifted(
    liftHookCallback(
      'src/components/features/management/schedules/scheduled-prefill/useScheduledPrefillContainers.ts',
      'useEffect',
      'const reconcile = async () =>'
    ),
    {
      opened: true,
      activeService: 'epic',
      persistentLoginTarget: null,
      login,
      privateAvailabilityIdentityRef: { current: 'owner' },
      persistentContainerByServiceRef: {
        current: new Map([
          ['Epic', { sessionId: 'epic-session', isRunning: true, isAuthenticated: false }]
        ])
      },
      hasActivePersistentLogin: () => false,
      isPersistentLoginDismissed: () => false,
      getPersistentServiceId: () => 'Epic',
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic', 'xbox'],
      reconcilePersistentLoginFromServer: async (...args) => {
        reconciled.push(args);
        return 'unavailable';
      },
      setPersistentLoginTarget: (value) => targets.push(value),
      t: (key) => key
    }
  );

  const cleanup = run();
  await flush();
  cleanup?.();
  assert.deepEqual(reconciled, []);
  assert.deepEqual(targets, []);

  login.current = {
    serviceKey: 'epic',
    sessionId: 'epic-session',
    identity: 'owner',
    visible: false
  };
  const reopenedCleanup = run();
  await flush();
  reopenedCleanup?.();
  assert.deepEqual(reconciled, []);
  assert.deepEqual(targets, []);
});

test('account and container session replacement revoke the matching admission', () => {
  const accountAdmission = {
    serviceKey: 'epic',
    sessionId: 'old-epic',
    identity: 'owner-a',
    visible: true
  };
  const accountLogin = { current: accountAdmission };
  const accountResets = [];
  let accountTarget = 'epic';
  effect(containerSource, 'privateAvailabilityIdentityAppliedRef.current ===', {
    privateAvailabilityIdentityAppliedRef: { current: 'owner-a' },
    privateAvailabilityIdentity: 'owner-b',
    login: accountLogin,
    getPersistentServiceId: () => 'Epic',
    getPersistentLoginState: () => ({ sessionId: 'old-epic' }),
    getPersistentLoginStartRequest: () => undefined,
    resetPersistentLoginState: (service) => accountResets.push(service),
    setPersistentLoginTarget: (update) => {
      accountTarget = update(accountTarget);
    },
    SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic', 'xbox'],
    isPersistentLoginIntegrationReuse: () => false,
    isScheduledPrefillAccountService: () => true
  })();
  assert.equal(accountAdmission.visible, false);
  assert.equal(accountLogin.current, null);
  assert.deepEqual(accountResets, ['Epic']);
  assert.equal(accountTarget, null);

  const sessionAdmission = {
    serviceKey: 'steam',
    sessionId: 'old-steam',
    identity: 'owner-b',
    visible: true
  };
  const sessionLogin = { current: sessionAdmission };
  const sessionResets = [];
  let sessionTarget = 'steam';
  effect(containerSource, 'const timers = stopCleanupTimersRef.current', {
    opened: true,
    stopCleanupTimersRef: { current: new Map() },
    login: sessionLogin,
    persistentContainerByService: new Map([
      ['Steam', { sessionId: 'new-steam', isRunning: true, isAuthenticated: false }]
    ]),
    persistentContainerByServiceRef: {
      current: new Map([
        ['Steam', { sessionId: 'new-steam', isRunning: true, isAuthenticated: false }]
      ])
    },
    PERSISTENT_PREFILL_SERVICES: [{ key: 'steam', service: 'Steam' }],
    resetPersistentLoginState: (service) => sessionResets.push(service),
    setPersistentLoginTarget: (update) => {
      sessionTarget = update(sessionTarget);
    },
    hasActivePersistentLogin: () => true,
    SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS: 1000,
    setTimeout,
    clearTimeout
  })();
  assert.equal(sessionAdmission.visible, false);
  assert.equal(sessionLogin.current, null);
  assert.deepEqual(sessionResets, ['Steam']);
  assert.equal(sessionTarget, null);
});

test('settled empty container status does not re-enter initial loading during refresh', async () => {
  const second = deferred();
  const responses = [Promise.resolve([]), second.promise];
  let snapshot = null;
  const loading = [];
  const load = arrow(containerSource, 'loadPersistentContainers', {
    persistentContainersRequestRef: { current: null },
    persistentContainersRevisionRef: { current: 0 },
    persistentContainersRef: { current: null },
    ApiService: {
      getPersistentPrefillContainers: () => responses.shift()
    },
    mergePrefillRuns: (_previous, next) => next,
    isAbortError: () => false,
    setPersistentContainers: (update) => {
      snapshot = typeof update === 'function' ? update(snapshot) : update;
    },
    setPersistentError: () => undefined,
    setLoadingPersistentContainers: (value) => loading.push(value)
  });

  await load();
  assert.deepEqual(snapshot, []);
  const pending = load();
  assert.equal(loading.at(-1), false);
  second.resolve([]);
  await pending;
});

test('container refresh failure retains its snapshot and still drains one trailing request', async () => {
  const first = deferred();
  const trailing = deferred();
  const responses = [first.promise, trailing.promise];
  let snapshot = [{ service: 'Epic', sessionId: 'existing', runs: [] }];
  let readError = null;
  let calls = 0;
  const load = arrow(containerSource, 'loadPersistentContainers', {
    persistentContainersRequestRef: { current: null },
    persistentContainersRevisionRef: { current: 0 },
    persistentContainersRef: { current: snapshot },
    ApiService: {
      getPersistentPrefillContainers: () => {
        calls += 1;
        return responses.shift();
      }
    },
    mergePrefillRuns: (_previous, next) => next,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    setPersistentContainers: (update) => {
      snapshot = update(snapshot);
    },
    setPersistentError: (value) => {
      readError = value;
    },
    setLoadingPersistentContainers: () => undefined
  });

  const pending = load();
  load();
  load();
  first.reject(new Error('stale failure'));
  await flush();
  assert.equal(calls, 2);
  assert.equal(snapshot[0].sessionId, 'existing');
  assert.equal(readError, null);

  trailing.resolve([{ service: 'Epic', sessionId: 'confirmed', runs: [] }]);
  await pending;
  assert.equal(calls, 2);
  assert.equal(snapshot[0].sessionId, 'confirmed');
  assert.equal(readError, null);
});

test('availability retains same-owner snapshots and isolates one service failure through retry', async () => {
  let availability = new Map([
    ['steam', { available: true }],
    ['epic', { available: true }],
    ['xbox', { available: false }]
  ]);
  let errors = {};
  let availabilityIdentity = 'owner';
  let errorsIdentity = 'owner';
  const loading = [];
  const replies = new Map([
    ['Steam', () => Promise.resolve({ available: false })],
    ['Epic', () => Promise.reject(new Error('epic unavailable'))],
    ['Xbox', () => Promise.resolve({ available: true })]
  ]);
  const bindings = {
    integrationLoginRequestRef: { current: null },
    privateAvailabilityIdentityRef: { current: 'owner' },
    integrationLoginAvailabilityIdentityRef: { current: 'owner' },
    integrationLoginErrorsIdentityRef: { current: 'owner' },
    canUseSavedLogin: true,
    requiresIndividualAccount: false,
    SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic', 'xbox'],
    getPersistentServiceId: (service) => service[0].toUpperCase() + service.slice(1),
    ApiService: {
      getPersistentIntegrationLoginAvailability: (service) => replies.get(service)()
    },
    isAbortError: () => false,
    getErrorMessage: (error) => error.message,
    setIntegrationLoginAvailabilityByService: (update) => {
      availability = update(availability);
    },
    setIntegrationLoginAvailabilityIdentity: (value) => {
      availabilityIdentity = value;
      bindings.integrationLoginAvailabilityIdentityRef.current = value;
    },
    setIntegrationLoginErrors: (update) => {
      errors = typeof update === 'function' ? update(errors) : update;
    },
    setIntegrationLoginErrorsIdentity: (value) => {
      errorsIdentity = value;
      bindings.integrationLoginErrorsIdentityRef.current = value;
    },
    setLoadingIntegrationLoginAvailability: (value) => loading.push(value)
  };
  const load = arrow(containerSource, 'loadIntegrationLoginAvailability', bindings);

  await load();
  assert.equal(availability.get('steam').available, false);
  assert.equal(availability.get('epic').available, true);
  assert.equal(availability.get('xbox').available, true);
  assert.equal(errors.epic, 'epic unavailable');
  assert.equal(availabilityIdentity, 'owner');
  assert.equal(errorsIdentity, 'owner');
  assert.equal(loading[0], false);

  const retry = deferred();
  replies.set('Epic', () => retry.promise);
  const pending = load();
  await flush();
  assert.equal(errors.epic, 'epic unavailable');
  retry.resolve({ available: false });
  await pending;
  assert.equal(availability.get('epic').available, false);
  assert.equal(errors.epic, undefined);
});

test('persistent popup close routes revoke only their bound visible admission synchronously', () => {
  for (const route of ['close', 'escape', 'backdrop', 'cancel']) {
    const admitted = {
      serviceKey: 'epic',
      sessionId: 'epic-session',
      identity: 'owner',
      visible: true
    };
    const login = { current: admitted };
    let state = { pendingChallenge: { challengeId: 'challenge' }, dismissed: false };
    let target = 'epic';
    const dismiss = arrow(containerSource, 'handleDismissPersistentLogin', {
      renderedLogin: admitted,
      login,
      privateAvailabilityIdentityRef: { current: 'owner' },
      persistentContainerByServiceRef: {
        current: new Map([['Epic', { sessionId: 'epic-session' }]])
      },
      getPersistentServiceId: () => 'Epic',
      updatePersistentLoginState: (_service, update) => {
        state = update(state);
      },
      setPersistentLoginTarget: (update) => {
        target = update(target);
      }
    });

    dismiss();
    assert.equal(admitted.visible, false, route);
    assert.equal(state.dismissed, true, route);
    assert.equal(state.pendingChallenge.challengeId, 'challenge', route);
    assert.equal(target, null, route);
  }
});

test('an old bound popup close cannot revoke an account, session, or explicit-click successor', () => {
  const admitted = {
    serviceKey: 'xbox',
    sessionId: 'old-session',
    identity: 'owner-a',
    visible: true
  };
  const successor = {
    serviceKey: 'xbox',
    sessionId: 'new-session',
    identity: 'owner-b',
    visible: true
  };
  const login = { current: successor };
  const writes = [];
  const dismiss = arrow(containerSource, 'handleDismissPersistentLogin', {
    renderedLogin: admitted,
    login,
    privateAvailabilityIdentityRef: { current: 'owner-b' },
    persistentContainerByServiceRef: {
      current: new Map([['Xbox', { sessionId: 'new-session' }]])
    },
    getPersistentServiceId: () => 'Xbox',
    updatePersistentLoginState: () => writes.push('store'),
    setPersistentLoginTarget: () => writes.push('target')
  });

  dismiss();
  assert.equal(successor.visible, true);
  assert.deepEqual(writes, []);
});

test('a later explicit login click resumes its owned attempt with a new presentation token', async () => {
  const prior = {
    serviceKey: 'epic',
    sessionId: 'epic-session',
    identity: 'owner',
    visible: false
  };
  const login = { current: prior };
  const calls = [];
  const handleLogin = arrow(containerSource, 'handlePersistentLogin', {
    act: async (_serviceKey, _action, run) => run(() => true),
    getPersistentServiceId: () => 'Epic',
    persistentContainerByServiceRef: {
      current: new Map([['Epic', { sessionId: 'epic-session', isRunning: true }]])
    },
    visibleIntegrationLoginAvailabilityByService: new Map(),
    visibleIntegrationLoginErrors: {},
    canUseSavedLogin: true,
    privateAvailabilityIdentity: 'owner',
    activeRef: { current: 'epic' },
    hasActivePersistentLogin: () => true,
    getPersistentLoginStartRequest: () => undefined,
    getPersistentLoginState: () => ({ sessionId: 'epic-session' }),
    resetPersistentLoginState: () => calls.push('reset'),
    setPersistentLoginStartSessionId: () => calls.push('start-session'),
    setPersistentLoginTarget: (value) => calls.push(['target', value]),
    requestPersistentLoginAttempt: () => calls.push('request'),
    baseKey,
    t: (key) => key,
    login
  });

  await handleLogin('epic', false);
  assert.notEqual(login.current, prior);
  assert.deepEqual(login.current, {
    serviceKey: 'epic',
    sessionId: 'epic-session',
    identity: 'owner',
    visible: true
  });
  assert.deepEqual(calls, [['target', 'epic'], 'request']);
});

test('same-service request nonces reset the host close guard before a batched reopen', () => {
  const runtime = hooks();
  let nonce = 1;
  let dismissals = 0;
  const render = component(persistentLoginHostSource, 'PersistentLoginHost', {
    useEffect: runtime.useEffect,
    useRef: runtime.useRef,
    useState: runtime.useState,
    SteamPersistentLogin: 'SteamPersistentLogin',
    EpicPersistentLogin: 'EpicPersistentLogin',
    XboxPersistentLogin: 'XboxPersistentLogin',
    SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS: 1000,
    getPersistentServiceId: () => 'Epic',
    usePersistentLoginStoreState: () => ({ sessionId: 'epic-session', pendingChallenge: null }),
    usePersistentLoginRequestNonce: () => nonce,
    hasUnconsumedLoginAttempt: () => true,
    setTimeout,
    clearTimeout
  });
  const props = {
    serviceKey: 'epic',
    isRunning: true,
    isAuthenticated: false,
    onAuthenticated: () => undefined,
    onDismiss: () => {
      dismissals += 1;
    }
  };

  let tree = runtime.render(render, props);
  tree.props.onDismiss();
  nonce += 1;
  tree = runtime.render(render, props);
  tree.props.onDismiss();
  assert.equal(dismissals, 2);
  runtime.unmount();
});

test('each persistent platform routes real modal close and footer cancellation to its owner', () => {
  for (const platform of persistentPlatformSources) {
    let dismissals = 0;
    const resumptions = [];
    const state = { loading: true, hasChallenge: false };
    const actions = { start: () => undefined };
    const render = component(platform.source, platform.name, {
      usePersistentLoginHost: ({ resumeModal }) => {
        resumeModal();
        return true;
      },
      usePersistentLoginStoreState: () => ({ loginDeadline: 123 }),
      [platform.hook]: () => ({
        state,
        actions,
        startLogin: actions.start,
        resumeModal: () => resumptions.push(platform.service)
      }),
      [platform.modal]: platform.modal
    });
    const tree = render({
      isRunning: true,
      isAuthenticated: false,
      onAuthenticated: () => undefined,
      autoStart: true,
      onDismiss: () => {
        dismissals += 1;
      }
    });
    tree.props.onClose();
    tree.props.onCancelLogin();
    assert.equal(dismissals, 2, platform.service);
    assert.deepEqual(resumptions, [platform.service]);
    assert.equal(tree.props.dismissBehavior, 'keep-pending', platform.service);
  }
});

test('Activity shows localized capacity only for concurrent service containers', () => {
  const messages = [];
  const render = component(activitySource, 'ScheduledPrefillActivityModal', {
    useTranslation: () => ({
      t: (key, values) => {
        messages.push([key, values]);
        return key;
      }
    }),
    useScrollAreaHeight: () => [() => undefined, 400],
    Modal: 'Modal',
    Button: 'Button',
    CustomScrollbar: 'Scrollbar',
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    supportsConcurrentPrefill: (container) => container.maxConcurrentRuns > 1,
    ScheduledPrefillDownloads: 'Downloads'
  });
  const tree = render({
    opened: true,
    disabled: false,
    onClose: () => undefined,
    containers: {
      containersByServiceKey: new Map([
        ['steam', { activeRunCount: 2, maxConcurrentRuns: 4 }],
        ['epic', { activeRunCount: 1, maxConcurrentRuns: 1 }]
      ]),
      cancellingRunIds: [],
      runErrors: {},
      handleCancelPersistentDownload: () => undefined
    }
  });
  assert.equal(walk(tree, 'section').length, 5);
  assert.deepEqual(
    messages.filter(([key]) => key === 'prefill.runs.capacity'),
    [['prefill.runs.capacity', { count: 2, limit: 4 }]]
  );
  assert.equal(messages.filter(([key]) => key === 'prefill.runs.capacityHelp').length, 1);
});

test('feature owner keeps actual container and challenge subscriptions balanced across dialogs', async () => {
  for (const source of [
    detailSource,
    configModalSource,
    containerModalSource,
    activitySource,
    sharedSource
  ]) {
    let owners = 0;
    const visit = (node) => {
      if (
        ts.isCallExpression(node) &&
        node.expression.getText(source) === 'useScheduledPrefillContainers'
      )
        owners++;
      ts.forEachChild(node, visit);
    };
    visit(source);
    assert.equal(owners, source === detailSource ? 1 : 0, source.fileName);
  }
  const runtime = hooks(),
    listeners = new Map(),
    changes = [];
  const t = (key) => key;
  const on = (event, handler) => {
    if (!listeners.has(event)) listeners.set(event, new Set());
    listeners.get(event).add(handler);
    changes.push(['on', event, handler]);
  };
  const off = (event, handler) => {
    assert.equal(listeners.get(event)?.delete(handler), true);
    changes.push(['off', event, handler]);
  };
  const useSignalR = () => ({ on, off, isConnected: true });
  const serviceIds = ['Steam', 'Epic', 'Xbox', 'BattleNet', 'Riot'];
  const getPersistentServiceId = (key) => serviceIds[order.indexOf(key)];
  const common = {
    ...runtime,
    useTranslation: () => ({ t }),
    useSignalR,
    useReconnectRefetch: () => undefined
  };
  const containerEvents = serviceIds.map((service) => service + 'DaemonSessionUpdated');
  const containerSignalR = component(
    containerSignalRSource,
    'usePersistentPrefillContainerSignalR',
    {
      ...common,
      useRefreshRate: () => ({ getRefreshInterval: () => 500 }),
      PERSISTENT_PREFILL_CONTAINER_SIGNALR_EVENTS: containerEvents
    }
  );
  const challengeSignalR = component(challengeSignalRSource, 'usePersistentLoginChallengeSignalR', {
    ...common,
    LOGIN_REQUIRED_SERVICE_IDS: ['Steam', 'Epic', 'Xbox'],
    getPersistentPrefillCredentialChallengeEvent: (service) => service + 'CredentialChallenge',
    getPersistentPrefillAuthStateChangedEvent: (service) => service + 'AuthStateChanged'
  });
  const bulkCalls = [];
  const ApiService = {
    getScheduledPrefillConfig: async () => configuration(),
    getScheduledPrefillSchedule: async () => [
      { ...record(), scheduleId: record().id, serviceId: 'Steam', nextRunUtc: null }
    ],
    getPersistentPrefillContainers: async () => [
      { service: 'Steam', sessionId: 'session', isRunning: true, isAuthenticated: false }
    ],
    getPersistentIntegrationLoginAvailability: async () => ({
      available: false,
      reason: 'not-signed-in'
    }),
    setScheduledPrefillSchedulesEnabled: async (enabled) => {
      bulkCalls.push(enabled);
      return configuration();
    }
  };
  const containers = component(containerSource, 'useScheduledPrefillContainers', {
    ...common,
    ApiService,
    useAuth: () => ({ authenticationEnabled: false, isLoading: false }),
    useSteamAuth: () => ({ revision: 0 }),
    getPersistentServiceId,
    SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS: ['steam', 'epic', 'xbox'],
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    PERSISTENT_PREFILL_SERVICES: order.map((key) => ({
      key,
      service: getPersistentServiceId(key)
    })),
    hasActivePersistentLogin: () => false,
    isPersistentLoginDismissed: () => true,
    resetPersistentLoginState: () => undefined,
    usePersistentLoginStoreVersion: () => undefined,
    usePersistentPrefillContainerSignalR: containerSignalR,
    usePersistentLoginChallengeSignalR: challengeSignalR,
    isAbortError: () => false,
    getErrorMessage: (error) => error.message
  });
  let calls = 0;
  const render = component(detailSource, 'ScheduledPrefillScheduleDetail', {
    ...primitives,
    ...common,
    ApiService,
    getPersistentServiceId,
    useScheduledPrefillContainers: (activeService) => {
      calls++;
      return containers(activeService);
    },
    SCHEDULED_PREFILL_SERVICE_RUN_ORDER: order,
    SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY: Object.fromEntries(
      serviceIds.map((service, index) => [service, order[index]])
    ),
    isScheduledPrefillAccountService: (key) => ['steam', 'epic', 'xbox'].includes(key),
    ScheduledPrefillServiceScheduleRow: 'Row',
    ScheduledPrefillConfigModal: 'Editor',
    ScheduledPrefillContainerModal: 'Container',
    ScheduledPrefillActivityModal: 'Activity',
    ScheduledPrefillSharedSettingsModal: 'Settings',
    ConfirmationModal: 'ConfirmationModal',
    PersistentLoginHost: 'Login',
    getErrorMessage: (error) => error.message,
    isAbortError: () => false,
    setInterval: () => 1,
    clearInterval: () => undefined
  });
  const props = {
    onRunNow: () => undefined,
    onRunService: () => undefined,
    isRunServicePending: () => false
  };
  let renders = 0;
  const draw = () => {
    renders++;
    return runtime.render(render, props);
  };
  draw();
  await flush();
  let tree = draw();
  const snapshot = () => [...listeners].map(([event, handlers]) => [event, handlers.size]);
  const baseline = snapshot();
  assert.equal(baseline.length, 14);
  assert.ok(baseline.every(([, count]) => count === 1));
  const registrations = changes.length;

  let globalMenu = walk(tree, 'ActionMenu')[0];
  const globalGroups = globalMenu.children.filter((node) => node.props.role === 'group');
  const globalDividers = globalMenu.children.filter((node) => node.props.role === 'separator');
  assert.equal(globalMenu.props.id, globalMenu.props.trigger.props['aria-controls']);
  assert.equal(globalMenu.props['aria-label'], 'management.actions.menuLabel');
  assert.equal(globalMenu.props.trigger.props['aria-expanded'], false);
  assert.equal(globalMenu.props.trigger.props['aria-haspopup'], undefined);
  assert.equal(globalGroups.length, 2);
  assert.equal(globalGroups[0].props['aria-labelledby'], undefined);
  assert.equal(globalGroups[1].children[0].props.id, globalGroups[1].props['aria-labelledby']);
  assert.equal(textOf(globalGroups[1].children[0]), `${baseKey}.bulkToggle.label`);
  assert.deepEqual(walk(globalMenu, 'ActionMenuItem').map(textOf), [
    `${baseKey}.actions.viewActivity`,
    `${baseKey}.actions.sharedSettings`,
    `${baseKey}.bulkToggle.enableAll`,
    `${baseKey}.bulkToggle.disableAll`
  ]);
  assert.equal(globalDividers.length, 1);
  assert.equal(globalDividers[0].props['aria-orientation'], 'horizontal');

  const globalFocus = [];
  globalMenu.props.trigger.props.ref.current = { focus: () => globalFocus.push(true) };
  globalMenu.props.trigger.props.onClick();
  tree = draw();
  globalMenu = walk(tree, 'ActionMenu')[0];
  assert.equal(globalMenu.props.trigger.props['aria-expanded'], true);
  let globalItems = walk(globalMenu, 'ActionMenuItem');
  globalItems[0].props.onClick();
  tree = draw();
  assert.equal(walk(tree, 'Activity')[0].props.opened, true);
  walk(tree, 'Activity')[0].props.onClose();
  tree = draw();

  globalMenu = walk(tree, 'ActionMenu')[0];
  globalItems = walk(globalMenu, 'ActionMenuItem');
  globalItems[1].props.onClick();
  tree = draw();
  assert.equal(walk(tree, 'Settings')[0].props.opened, true);
  walk(tree, 'Settings')[0].props.onClose();
  tree = draw();

  globalMenu = walk(tree, 'ActionMenu')[0];
  globalItems = walk(globalMenu, 'ActionMenuItem');
  globalItems[2].props.onClick();
  await flush();
  globalItems[3].props.onClick();
  await flush();
  assert.deepEqual(bulkCalls, [true, false]);
  assert.equal(globalFocus.length, 4);

  for (let index = 0; index < 3; index++) {
    walk(tree, 'Row')[0].props.onOpen('steam', record().id);
    tree = draw();
    assert.ok(walk(tree, 'Editor')[0].props.target);
    walk(tree, 'Editor')[0].props.onClose();
    tree = draw();
    walk(tree, 'ActionMenuItem')[0].props.onClick();
    tree = draw();
    assert.equal(walk(tree, 'Activity')[0].props.opened, true);
    walk(tree, 'Activity')[0].props.onClose();
    tree = draw();
    walk(tree, 'Row')[0].props.onContainer('steam');
    tree = draw();
    assert.equal(walk(tree, 'Container')[0].props.serviceKey, 'steam');
    walk(tree, 'Container')[0].props.onClose();
    tree = draw();
    assert.deepEqual(snapshot(), baseline);
  }
  assert.equal(calls, renders);
  assert.equal(changes.length, registrations);
  runtime.unmount();
  assert.ok([...listeners.values()].every((handlers) => handlers.size === 0));
  assert.equal(
    changes.filter(([action]) => action === 'on').length,
    changes.filter(([action]) => action === 'off').length
  );
});

test('Actions Escape closes only the menu and restores focus to its trigger', () => {
  const escapeHandler = findSoleNode(
    actionMenuSource,
    'ActionMenu capture Escape handler',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === 'handleEscape' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);
  const closeCalls = [];
  const stopCalls = [];
  const focusCalls = [];
  const handleEscape = bindLifted(escapeHandler, {
    onClose: () => closeCalls.push(true),
    requestAnimationFrame: (callback) => callback(),
    focusTrigger: () => focusCalls.push(true)
  });

  handleEscape({ key: 'Escape', stopPropagation: () => stopCalls.push(true) });
  assert.deepEqual(stopCalls, [true]);
  assert.deepEqual(closeCalls, [true]);
  assert.deepEqual(focusCalls, [true]);

  handleEscape({ key: 'Enter', stopPropagation: () => stopCalls.push(true) });
  assert.deepEqual(closeCalls, [true]);
});

test('Actions Tab traverses nested native-button groups and exits without stranding focus', () => {
  const triggerHandler = findSoleNode(
    actionMenuSource,
    'ActionMenu trigger Tab handler',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === 'handleTriggerKeyDown' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);
  const menuHandler = findSoleNode(
    actionMenuSource,
    'ActionMenu item Tab handler',
    (node) =>
      ts.isVariableDeclaration(node) &&
      node.name.getText(actionMenuSource) === 'handleMenuKeyDown' &&
      node.initializer !== undefined &&
      ts.isArrowFunction(node.initializer)
  ).initializer.getText(actionMenuSource);
  const firstItem = {
    offsetParent: {},
    focusCalls: 0,
    focus() {
      this.focusCalls += 1;
    }
  };
  const middleItem = { offsetParent: {}, focus: () => undefined };
  const lastItem = { offsetParent: {}, focus: () => undefined };
  const groupLabel = { offsetParent: {}, role: undefined };
  const divider = { offsetParent: {}, role: 'separator' };
  const menuRoot = {
    groups: [{ children: [groupLabel, firstItem, middleItem] }, { children: [divider, lastItem] }],
    querySelectorAll: (selector) => {
      assert.match(selector, /button:not\(\[disabled\]\)/);
      return [firstItem, middleItem, lastItem];
    }
  };
  assert.deepEqual(getFocusable(menuRoot), [firstItem, middleItem, lastItem]);
  const entered = [];
  const triggerClosed = [];
  const handleTriggerKeyDown = bindLifted(triggerHandler, {
    isOpen: true,
    dropdownRef: { current: menuRoot },
    getFocusable,
    onClose: () => triggerClosed.push(true)
  });

  handleTriggerKeyDown({
    key: 'Tab',
    shiftKey: false,
    preventDefault: () => entered.push('prevent'),
    stopPropagation: () => entered.push('stop')
  });
  assert.deepEqual(entered, ['prevent', 'stop']);
  assert.equal(firstItem.focusCalls, 1, 'Tab enters the first enabled item');

  const reverse = [];
  handleTriggerKeyDown({
    key: 'Tab',
    shiftKey: true,
    preventDefault: () => reverse.push('prevent'),
    stopPropagation: () => reverse.push('stop')
  });
  assert.deepEqual(reverse, [], 'Shift+Tab keeps native reverse traversal');
  assert.deepEqual(triggerClosed, [true], 'Shift+Tab closes the open menu');
  assert.match(
    actionMenuSource.text,
    /import \{ getFocusable \} from '@utils\/focus';/,
    'Actions uses the shared focus query'
  );

  const closed = [];
  const triggerFocus = [];
  const nextFocus = [];
  const handleMenuKeyDown = bindLifted(menuHandler, {
    dropdownRef: { current: menuRoot },
    getFocusable,
    onClose: () => closed.push(true),
    focusTrigger: () => triggerFocus.push(true),
    focusNextAfterTrigger: () => nextFocus.push(true)
  });
  const shifted = [];
  handleMenuKeyDown({
    key: 'Tab',
    shiftKey: true,
    target: firstItem,
    preventDefault: () => shifted.push('prevent'),
    stopPropagation: () => shifted.push('stop')
  });
  assert.deepEqual(shifted, ['prevent', 'stop']);
  assert.deepEqual(closed, [true]);
  assert.deepEqual(triggerFocus, [true]);

  const forward = [];
  handleMenuKeyDown({
    key: 'Tab',
    shiftKey: false,
    target: lastItem,
    preventDefault: () => forward.push('prevent'),
    stopPropagation: () => forward.push('stop')
  });
  assert.deepEqual(forward, ['prevent', 'stop']);
  assert.deepEqual(closed, [true, true]);
  assert.deepEqual(nextFocus, [true]);

  const middle = [];
  handleMenuKeyDown({
    key: 'Tab',
    shiftKey: false,
    target: middleItem,
    preventDefault: () => middle.push('prevent'),
    stopPropagation: () => middle.push('stop')
  });
  assert.deepEqual(middle, [], 'intermediate menu items retain native Tab order');
});
