import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import typescript from 'typescript';
import { hookStore, turn } from './component-hooks.mjs';
import { featureTree, inert } from './component-tree.mjs';
import {
  bindLifted,
  compileTree,
  findSoleNode,
  liftHookCallback,
  make,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * A user-role account changes only itself, so the page offers it none of the controls that change
 * the whole installation: the server refuses each of them for it. An admin keeps every one. Each
 * case draws the shipped code once as an admin and once as a user-role account.
 */

const translate = translator('en');

/** The elements in a drawn tree that `matches` accepts. */
const elementsIn = (node, matches) => {
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      if (matches(value)) found.push(value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  return found;
};

test('the /metrics switch and its Reset are read-only for a user-role account and live for an admin', () => {
  const ToggleSwitch = inert('ToggleSwitch');
  const Button = inert('Button');
  const drawAs = (sessionType) => {
    const { hooks, draw } = hookStore();
    const MetricsAccessSwitch = make(
      'src/components/features/management/grafana/MetricsAccessSwitch.tsx',
      'MetricsAccessSwitch',
      {
        React: { ...React, ...hooks },
        ...hooks,
        ToggleSwitch,
        Button,
        Lock: inert('Lock'),
        Unlock: inert('Unlock'),
        ApiService: { setMetricsSecurity: async () => undefined },
        useAuth: () => ({ isAdmin: true, sessionType }),
        useErrorHandler: () => ({ notifyError: () => undefined }),
        useTranslation: () => ({ t: translate })
      }
    );
    const tree = draw(() =>
      MetricsAccessSwitch({
        security: {
          requiresAuthentication: true,
          source: 'ui',
          canToggle: true,
          envVarValue: false
        },
        setSecurity: () => undefined
      })
    );
    const [toggle] = elementsIn(tree, (element) => element.type === ToggleSwitch);
    return {
      disabled: toggle.props.disabled,
      title: toggle.props.title,
      resets: elementsIn(tree, (element) => element.type === Button).length
    };
  };

  const admin = drawAs('admin');
  assert.equal(admin.disabled, false, 'an admin gets a live switch');
  assert.equal(admin.resets, 1, 'an admin gets Reset to default');

  const member = drawAs('user');
  assert.equal(member.disabled, true, 'a user-role account got a live /metrics switch');
  assert.equal(member.resets, 0, 'a user-role account got Reset to default');
  assert.equal(member.title, translate('management.grafana.metricsToggle.adminRequired'));
});

test('the prefill cache Clear is offered to an admin and not to a user-role account', () => {
  const panel = parseSource('src/components/features/prefill/PrefillPanel.tsx');
  const attribute = findSoleNode(
    panel,
    'onClearAllCache attribute',
    (node) => typescript.isJsxAttribute(node) && node.name.getText(panel) === 'onClearAllCache'
  );
  const expression = attribute.initializer.expression.getText(panel);
  const handleClearAllFromCache = () => undefined;
  const offered = (sessionType) =>
    bindLifted(`(isAdmin, sessionType) => (${expression})`, { handleClearAllFromCache })(
      true,
      sessionType
    );

  assert.equal(offered('admin'), handleClearAllFromCache, 'an admin lost the prefill cache Clear');
  assert.equal(
    offered('user'),
    undefined,
    'a user-role account was offered the prefill cache Clear'
  );
});

test('the prefill Utilities menu, which holds only Clear Cache DB, is drawn for an admin and not for a user-role account', () => {
  const panel = parseSource('src/components/features/prefill/PrefillPanel.tsx');
  const attribute = findSoleNode(
    panel,
    'isUserAuthenticated attribute',
    (node) => typescript.isJsxAttribute(node) && node.name.getText(panel) === 'isUserAuthenticated'
  );
  const authenticatedFor = bindLifted(
    `(isAdmin, sessionType) => (${attribute.initializer.expression.getText(panel)})`,
    {}
  );
  const SectionActionsMenu = inert('SectionActionsMenu');
  const PrefillCommandButtons = make(
    'src/components/features/prefill/PrefillCommandButtons.tsx',
    'PrefillCommandButtons',
    {
      React,
      useTranslation: () => ({ t: translate }),
      Button: inert('Button'),
      Card: inert('Card'),
      Badge: inert('Badge'),
      EnhancedDropdown: inert('EnhancedDropdown'),
      MultiSelectDropdown: inert('MultiSelectDropdown'),
      SectionActionsMenu,
      ActionMenuDangerItem: inert('ActionMenuDangerItem'),
      LoadingSpinner: inert('LoadingSpinner'),
      formatBytes: String,
      Database: inert('Database'),
      SELECTION_COMMANDS: [],
      PREFILL_COMMANDS: [{ id: 'prefill', icon: inert('Play'), variant: 'filled' }],
      UTILITY_COMMANDS: [{ id: 'clear-cache-data', icon: inert('Database'), authOnly: true }],
      OS_OPTIONS: [],
      getThreadOptions: () => []
    }
  );
  const menusFor = (sessionType) =>
    elementsIn(
      PrefillCommandButtons({
        isLoggedIn: true,
        isExecuting: false,
        isPrefillActive: false,
        isSessionActive: true,
        isUserAuthenticated: authenticatedFor(true, sessionType),
        selectedAppIds: [],
        selectedOS: [],
        maxConcurrency: 'auto',
        supportedCommands: ['prefill'],
        supportedOperatingSystems: [],
        cachedAppIds: [],
        estimatedSize: { bytes: 0, loading: false },
        onCommandClick: () => undefined,
        onSelectedOSChange: () => undefined,
        onMaxConcurrencyChange: () => undefined
      }),
      (element) => element.type === SectionActionsMenu
    ).length;

  assert.equal(menusFor('admin'), 1, 'an admin lost the Utilities menu with Clear Cache DB');
  assert.equal(
    menusFor('user'),
    0,
    'a user-role account was offered Clear Cache DB, which the server refuses for it'
  );
});

test('the image cache refresh and the full-scan rebuild are offered to an admin, not a user-role account', () => {
  const downloads = parseSource('src/components/features/downloads/DownloadsTab.tsx');
  const imageCacheItem = findSoleNode(
    downloads,
    'image cache item condition',
    (node) =>
      typescript.isBinaryExpression(node) &&
      node.operatorToken.kind === typescript.SyntaxKind.AmpersandAmpersandToken &&
      typescript.isParenthesizedExpression(node.right) &&
      node.right.getText(downloads).includes('handleClearImageCache()')
  );
  const imageCacheOffered = (sessionType) =>
    bindLifted(`(isGuest, sessionType) => (${imageCacheItem.left.getText(downloads)})`, {})(
      false,
      sessionType
    );
  assert.equal(imageCacheOffered('admin'), true, 'an admin lost the image cache refresh');
  assert.equal(
    imageCacheOffered('user'),
    false,
    'a user-role account was offered the image cache refresh'
  );

  const app = parseSource('src/App.tsx');
  const modal = findSoleNode(
    app,
    'FullScanRequiredModal',
    (node) =>
      typescript.isJsxOpeningLikeElement(node) &&
      node.tagName.getText(app) === 'FullScanRequiredModal'
  );
  const opened = modal.attributes.properties.find(
    (attribute) => typescript.isJsxAttribute(attribute) && attribute.name.getText(app) === 'opened'
  );
  const rebuildOffered = (sessionType) =>
    bindLifted(
      `(showFullScanRequiredModal, authMode, sessionType) => (${opened.initializer.expression.getText(app)})`,
      {}
    )(true, 'authenticated', sessionType);
  assert.equal(rebuildOffered('admin'), true, 'an admin lost the full-scan rebuild prompt');
  assert.equal(
    rebuildOffered('user'),
    false,
    'a user-role account was offered the full-scan rebuild'
  );
});

test('the Management page shows a user-role account the admin-only card, and an admin the section', () => {
  const sectionNames = [
    'SettingsSection',
    'IntegrationsSection',
    'StorageSection',
    'DataSection',
    'SchedulesSection',
    'PreferencesSection',
    'ClientsSection',
    'PrefillSessionsSection',
    'StatusCheckSection'
  ];
  const sections = Object.fromEntries(sectionNames.map((name) => [name, inert(name)]));
  const Card = inert('Card');
  const drawAs = (sessionType, section) => {
    const { hooks, draw } = hookStore();
    const ManagementTab = make(
      'src/components/features/management/ManagementTab.tsx',
      'ManagementTab',
      {
        React: { ...React, ...hooks, useDeferredValue: (value) => value },
        ...hooks,
        useDeferredValue: (value) => value,
        ...sections,
        Card,
        ManagementNav: inert('ManagementNav'),
        ErrorBoundary: ({ children }) => children,
        AccordionGroupProvider: ({ children }) => children,
        useTranslation: () => ({ t: (key) => key }),
        useStats: () => ({ refreshStats: () => undefined }),
        useMockMode: () => ({ mockMode: false }),
        useAuth: () => ({ isAdmin: true, authMode: 'authenticated', sessionType }),
        useTimeoutCallback: () => () => undefined,
        useErrorHandler: () => ({ notifyError: () => undefined }),
        useNotifySuccess: () => ({ notifySuccess: () => undefined }),
        storage: { getItem: () => section, setItem: () => undefined },
        readScanJump: () => null,
        clearScanJump: () => undefined
      }
    );
    const tree = draw(() => ManagementTab({}));
    const text = elementsIn(tree, (element) => element.type === 'p').map(
      (element) => element.props.children
    );
    const drawn = sectionNames.filter(
      (name) => elementsIn(tree, (element) => element.type === sections[name]).length > 0
    );
    return { text, drawn };
  };

  for (const section of [
    'integrations',
    'storage',
    'data',
    'schedules',
    'preferences',
    'clients',
    'status-check'
  ]) {
    const member = drawAs('user', section);
    assert.deepEqual(member.drawn, [], `a user-role account was drawn the ${section} section`);
    assert.ok(
      member.text.includes('management.sections.adminOnly'),
      `a user-role account on ${section} did not get the admin-only card`
    );
    assert.notDeepEqual(drawAs('admin', section).drawn, [], `an admin lost the ${section} section`);
  }
  assert.deepEqual(drawAs('user', 'settings').drawn, ['SettingsSection']);
  assert.deepEqual(drawAs('user', 'prefill-sessions').drawn, ['PrefillSessionsSection']);
});

test('the header refresh rate locks for a user-role account whose session type arrives after its sign-in', async () => {
  const { hooks, draw } = hookStore();
  let auth = { authMode: 'authenticated', sessionId: 'session-1', sessionType: null };
  const ContextValue = inert('ContextValue');
  const shippedFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(JSON.stringify({ refreshRate: 'RELAXED' }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    });
  try {
    const RefreshRates = make('src/contexts/RefreshRateContext.tsx', 'RefreshRateProvider', {
      React: { ...React, ...hooks },
      ...hooks,
      i18n: { t: (key) => key },
      APP_EVENTS: {},
      REFRESH_RATES: { LIVE: 1000, STANDARD: 10000, RELAXED: 30000 },
      ApiService: { getJsonFetchOptions: () => ({}) },
      assertOk: async () => undefined,
      getErrorMessage: (error) => String(error),
      useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
      useReconnectRefetch: () => undefined,
      useAuth: () => auth,
      useSessionPreferences: () => ({
        currentPreferences: null,
        error: null,
        resyncPreferences: () => undefined
      }),
      RefreshRateContext: { Provider: ContextValue }
    });
    const locked = async () => {
      draw(() => RefreshRates({ children: null }));
      await turn();
      const [element] = elementsIn(
        draw(() => RefreshRates({ children: null })),
        (node) => node.type === ContextValue
      );
      return element.props.value.isControlledByAdmin;
    };

    assert.equal(await locked(), false, 'an account holder whose type is not known yet was locked');
    auth = { ...auth, sessionType: 'user' };
    assert.equal(await locked(), true, 'a user-role account kept an unlocked refresh rate');
    auth = { ...auth, sessionType: 'admin' };
    assert.equal(await locked(), false, 'an admin was locked out of the refresh rate');
  } finally {
    globalThis.fetch = shippedFetch;
  }
});

test('a user-role account closes a kept card on its own screen without asking the server', async () => {
  const { isTerminalNotificationStatus } = await import(
    await compileTree('../src/contexts/notifications/notificationStatus.ts')
  );
  const keptRun = {
    id: 'op-1',
    type: 'cache_clearing',
    status: 'failed',
    details: { operationId: 'op-1', closeOperationIds: ['op-1'] }
  };
  const keptBulk = {
    id: 'bulk-1',
    type: 'bulk_removal',
    status: 'completed',
    details: { closeOperationIds: ['op-2', 'op-3'] }
  };
  const dismissAs = async (sessionType, card) => {
    const closed = [];
    const removed = [];
    const dismiss = bindLifted(
      liftHookCallback(
        'src/components/common/UniversalNotificationBar.tsx',
        'useCallback',
        'closeOperationIds'
      ),
      {
        notificationsRef: { current: [card] },
        mayChangeRunsRef: { current: sessionType !== 'user' },
        isTerminalNotificationStatus,
        hideNotification: () => assert.fail('a kept card is not hidden'),
        removeNotification: (...call) => removed.push(call),
        setDismissingIds: () => undefined,
        setTimeout: (callback) => callback(),
        NOTIFICATION_ANIMATION_DURATION_MS: 0,
        ApiService: {
          closeOperation: async (id) => {
            closed.push(id);
          }
        },
        notifyToastError: () => undefined,
        i18n: { t: (key) => key }
      }
    );
    dismiss(card.id);
    await turn();
    return { closed, removed };
  };

  for (const card of [keptRun, keptBulk]) {
    const member = await dismissAs('user', card);
    assert.deepEqual(member.closed, [], `a user-role account asked the server to close ${card.id}`);
    assert.deepEqual(
      member.removed,
      [[card.id, card.details.closeOperationIds]],
      `${card.id} did not leave a user-role account's screen`
    );
    const admin = await dismissAs('admin', card);
    assert.deepEqual(
      admin.closed,
      card.details.closeOperationIds,
      `an admin's close of ${card.id} did not reach the server`
    );
  }
});

test('the events page offers a user-role account no new event, no clickable day, no Edit and a read-only dialog', () => {
  const EventCalendar = inert('EventCalendar');
  const EventList = inert('EventList');
  const EventModal = inert('EventModal');
  const Button = inert('Button');
  const drawAs = (sessionType, viewMode) => {
    const { hooks, draw } = hookStore();
    const EventsTab = make('src/components/features/events/index.tsx', 'EventsTab', {
      React: { ...React, ...hooks },
      ...hooks,
      useState: (initial) =>
        initial === 'calendar' ? [viewMode, () => undefined] : hooks.useState(initial),
      EventCalendar,
      EventList,
      EventModal,
      Button,
      Card: ({ children }) => children,
      ErrorBlock: inert('ErrorBlock'),
      SegmentedControl: inert('SegmentedControl'),
      LoadingState: inert('LoadingState'),
      Plus: inert('Plus'),
      List: inert('List'),
      LayoutGrid: inert('LayoutGrid'),
      eventColorToken: () => 'primary',
      themeColorVar: () => 'var(--theme-primary)',
      useTranslation: () => ({ t: (key) => key }),
      useEvents: () => ({
        events: [],
        activeEvents: [],
        loading: false,
        error: null,
        refreshEvents: () => undefined
      }),
      useAuth: () => ({ sessionType })
    });
    const tree = draw(() => EventsTab({}));
    const one = (type) => elementsIn(tree, (element) => element.type === type)[0];
    return {
      newEvent: elementsIn(tree, (element) => element.type === Button).length,
      calendar: one(EventCalendar),
      list: one(EventList),
      modal: one(EventModal)
    };
  };

  const member = drawAs('user', 'calendar');
  assert.equal(member.newEvent, 0, 'a user-role account got the New event button');
  assert.equal(
    member.calendar.props.onDayClick,
    undefined,
    'a user-role account can add an event from a day'
  );
  assert.equal(
    member.modal.props.readOnly,
    true,
    "a user-role account's event dialog can be edited"
  );
  assert.equal(
    drawAs('user', 'list').list.props.onEventClick,
    undefined,
    'a user-role account got Edit in the list'
  );

  const admin = drawAs('admin', 'calendar');
  assert.equal(admin.newEvent, 1, 'an admin lost the New event button');
  assert.equal(
    typeof admin.calendar.props.onDayClick,
    'function',
    'an admin cannot add an event from a day'
  );
  assert.equal(admin.modal.props.readOnly, false, "an admin's event dialog is read-only");
  assert.equal(
    typeof drawAs('admin', 'list').list.props.onEventClick,
    'function',
    'an admin lost Edit in the list'
  );
});

test('a read-only event dialog disables every field and draws no Save or Delete', () => {
  const Button = inert('Button');
  // A form field hands its control a render function; drawing it here puts the control in the tree.
  const FormField = ({ children }) => children({ id: 'field' });
  const event = {
    id: 7,
    name: 'LAN night',
    description: 'Bring cables',
    startTimeUtc: '2026-11-20T18:00:00Z',
    endTimeUtc: '2026-11-20T23:00:00Z',
    colorIndex: 2
  };
  const drawAs = (readOnly) => {
    const { hooks, draw } = hookStore();
    const EventModal = make('src/components/features/events/EventModal.tsx', 'EventModal', {
      React: { ...React, ...hooks },
      ...hooks,
      noAutofill: {},
      Modal: ({ title, children }) => React.createElement('div', null, title, children),
      ConfirmationModal: inert('ConfirmationModal'),
      Button,
      Alert: inert('Alert'),
      FormField,
      DateTimePicker: inert('DateTimePicker'),
      CalendarDays: inert('CalendarDays'),
      Trash2: inert('Trash2'),
      Calendar: inert('Calendar'),
      Check: inert('Check'),
      useTranslation: () => ({ t: (key) => key }),
      useEvents: () => ({
        createEvent: () => assert.fail('a read-only dialog created an event'),
        updateEvent: () => assert.fail('a read-only dialog saved an event'),
        deleteEvent: () => assert.fail('a read-only dialog deleted an event')
      }),
      useReaderClock: () => ({}),
      useTimeFilter: () => ({
        setTimeRange: () => undefined,
        setSelectedEventIds: () => undefined
      }),
      formatTimestamp: () => 'Nov 20',
      getEventColorVar: () => 'var(--theme-primary)',
      getErrorMessage: (error) => String(error),
      APP_EVENTS: {}
    });
    const drawFields = (node) => {
      if (Array.isArray(node)) return node.map(drawFields);
      if (!React.isValidElement(node)) return node;
      if (node.type === FormField) return drawFields(FormField(node.props));
      const props = Object.fromEntries(
        Object.entries(node.props).map(([key, value]) => [key, drawFields(value)])
      );
      return { ...node, props };
    };
    const tree = drawFields(
      draw(() =>
        EventModal({
          opened: true,
          event,
          readOnly,
          onClose: () => undefined,
          onSave: () => undefined
        })
      )
    );
    const controls = elementsIn(
      tree,
      (element) =>
        element.type === 'input' ||
        element.type === 'textarea' ||
        (element.type === 'button' && element.props.type === 'button') ||
        (element.type === Button &&
          element.props.variant === 'transparent' &&
          element.props.type === 'button')
    ).filter(
      (element) => element.props.onClick !== undefined || element.props.onChange !== undefined
    );
    const fieldControls = controls.filter(
      (element) => element.props.children !== 'events.modal.actions.viewStats'
    );
    const actions = elementsIn(
      tree,
      (element) =>
        element.type === Button &&
        (element.props.type === 'submit' || element.props.color === 'destructive')
    );
    return {
      fieldControls,
      actions,
      title: elementsIn(tree, (element) => element.type === 'span')[0].props.children
    };
  };

  const member = drawAs(true);
  assert.ok(
    member.fieldControls.length >= 12,
    `found ${member.fieldControls.length} field controls`
  );
  for (const control of member.fieldControls)
    assert.equal(
      control.props.disabled,
      true,
      `a read-only dialog left a ${String(control.type.name ?? control.type)} live`
    );
  assert.equal(member.actions.length, 0, 'a read-only dialog drew Save or Delete');
  assert.equal(member.title, 'LAN night');

  const admin = drawAs(false);
  for (const control of admin.fieldControls)
    assert.notEqual(control.props.disabled, true, "an admin's event dialog has a disabled field");
  assert.equal(admin.actions.length, 2, "an admin's event dialog lost Save or Delete");
  assert.equal(admin.title, 'events.modal.editTitle');
});

test('a calendar without a day handler draws its future days plain, with no click and no hover look', async () => {
  const drawDays = async (onDayClick) => {
    const { hooks, draw } = hookStore();
    const { Root: EventCalendar, draw: drawFeatures } = await featureTree(
      'src/components/features/events/EventCalendar.tsx',
      'EventCalendar',
      {
        React: { ...React, ...hooks },
        ...hooks,
        createPortal: () => null,
        useTranslation: () => ({ t: (key) => key }),
        useTimezone: () => ({ useLocalTimezone: false, useUtcTimezone: true }),
        useCalendarSettings: () => ({
          settings: {
            weekStartDay: 'sunday',
            hideEndedEvents: false,
            showWeekNumbers: false,
            compactMode: false,
            showAdjacentMonths: false,
            eventDisplayStyle: 'spanning',
            eventOpacity: 100
          }
        }),
        useMediaQuery: () => false,
        useAnchoredPanel: () => ({ present: false, closing: false, position: null })
      }
    );
    const tree = draw(() =>
      drawFeatures(EventCalendar({ events: [], onEventClick: () => undefined, onDayClick }))
    );
    return elementsIn(
      tree,
      (element) =>
        typeof element.props.className === 'string' &&
        element.props.className.includes('calendar-day ') &&
        !element.props.className.includes('calendar-day--past')
    );
  };

  const plain = await drawDays(undefined);
  assert.ok(plain.length > 0, 'the calendar drew no future day');
  for (const day of plain) {
    assert.equal(day.props.onClick, undefined, 'a future day is clickable with no day handler');
    assert.ok(
      day.props.className.includes('calendar-day--static'),
      `a future day reads ${day.props.className}`
    );
    assert.ok(!day.props.className.includes('group'), 'a future day keeps the hover look');
  }
  const clickable = await drawDays(() => undefined);
  for (const day of clickable) {
    assert.equal(typeof day.props.onClick, 'function', "an admin's future day lost its click");
    assert.ok(day.props.className.includes('group'), "an admin's future day lost its hover look");
  }
});
