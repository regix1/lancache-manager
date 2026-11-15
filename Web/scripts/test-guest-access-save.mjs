import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { compileTree, make, moduleUrl } from './transpile-module.mjs';

/**
 * The Guest access pane holds every change until its one Save button sends them. A change sends no
 * request; Save sends only the settings that changed, shows busy on itself alone, and a setting the
 * server refused keeps its change and names itself above Save.
 *
 * The whole shipped component runs here on a small hook store, and the test reads the props each
 * control and the Save button are drawn with, so it does not care how the component keeps state.
 */

const COMPONENT = 'src/components/features/user/GuestConfiguration.tsx';

/** The row label key of each default-preference toggle, keyed by the preference it saves. */
const ROW_LABELS = {
  sharpCorners: 'user.guest.preferences.sharpCorners.label',
  disableTooltips: 'activeSessions.preferencesModal.tooltips',
  showDatasourceLabels: 'user.guest.preferences.datasourceLabels.label',
  signIn: 'user.panes.prefs.newGuestsCanSignIn'
};

/** The service icons are drawings; the pane only passes them on. */
const serviceIcons = Object.fromEntries(
  ['SteamIcon', 'EpicIcon', 'BlizzardIcon', 'RiotIcon', 'XboxIcon'].map((name) => [
    `@components/ui/${name}`,
    moduleUrl(`export const ${name} = () => null;`)
  ])
);

const [
  onOff,
  threads,
  timeFormats,
  userTypes,
  guestPrefill,
  prefillServices,
  timezone,
  pending,
  drafts
] = await Promise.all(
  [
    '../src/components/features/user/onOffOptions.ts',
    '../src/components/features/user/threadOptions.ts',
    '../src/components/features/user/timeFormatOptions.ts',
    '../src/components/features/user/types.ts',
    '../src/components/features/user/guestPrefillConfig.ts',
    '../src/components/features/prefill/hooks/prefillServiceConfig.ts',
    '../src/contexts/TimezoneContext.types.ts',
    '../src/utils/pendingPreferences.ts',
    '../src/components/features/user/draftEdits.ts'
  ].map(async (path) => import(await compileTree(path, serviceIcons)))
);

const deferred = () => {
  let resolve;
  const promise = new Promise((done) => {
    resolve = done;
  });
  return { promise, resolve };
};

/** Lets every handler continuation that is already due run. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

const ToggleSwitch = stub('ToggleSwitch');
const UserSettingRow = stub('UserSettingRow');
const Button = stub('Button');
const Alert = stub('Alert');

/**
 * Runs the component on one hook store. Effects are not run: the pane starts with its defaults
 * shown, and nothing here depends on a load. `lockCalls` records each sign-in lock the host stored.
 */
const mountPane = () => {
  const slots = [];
  let cursor = 0;
  const lockCalls = [];
  const hooks = {
    useState: (initial) => {
      const index = cursor++;
      const slot = (slots[index] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => {
      const index = cursor++;
      return (slots[index] ??= { current: initial });
    },
    useMemo: (build) => {
      const index = cursor++;
      return (slots[index] ??= { value: build() }).value;
    },
    useCallback: (callback) => {
      cursor++;
      return callback;
    },
    useEffect: () => {
      cursor++;
    },
    useLayoutEffect: () => {
      cursor++;
    }
  };
  const Pane = make(COMPONENT, 'GuestConfiguration', {
    React,
    ...hooks,
    useTranslation: () => ({ t: (key) => key }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useReconnectRefetch: () => undefined,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useAuth: () => ({ authMode: 'authenticated', isAdmin: true }),
    RotateCcw: stub('RotateCcw'),
    Eraser: stub('Eraser'),
    HelpPopover: stub('HelpPopover'),
    RowActionsMenu: stub('RowActionsMenu'),
    ActionMenuItem: stub('ActionMenuItem'),
    ActionMenuDivider: stub('ActionMenuDivider'),
    ErrorBlock: stub('ErrorBlock'),
    Alert,
    Button,
    EnhancedDropdown: stub('EnhancedDropdown'),
    MultiSelectDropdown: stub('MultiSelectDropdown'),
    ToggleSwitch,
    Tooltip: stub('Tooltip'),
    ConfirmationModal: stub('ConfirmationModal'),
    LoadingSpinner: stub('LoadingSpinner'),
    UserPaneHeader: stub('UserPaneHeader'),
    UserPaneSection: stub('UserPaneSection'),
    UserSettingRow,
    ApiService: {
      getFetchOptions: (options) => ({ ...options }),
      getJsonFetchOptions: (body, options) => ({ ...options, body: JSON.stringify(body) })
    },
    assertOk: async (response) => {
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return response;
    },
    getErrorMessage: (error) => String(error),
    PREFILL_SERVICES: prefillServices.PREFILL_SERVICES,
    prefillServiceRecord: prefillServices.prefillServiceRecord,
    TIME_SETTING_VALUES: timezone.TIME_SETTING_VALUES,
    clockFromTimeSetting: pending.clockFromTimeSetting,
    timeSettingFromClock: pending.timeSettingFromClock,
    durationOptions: userTypes.durationOptions,
    refreshRateOptions: userTypes.refreshRateOptions,
    showToast: () => undefined,
    getOnOffOptions: onOff.getOnOffOptions,
    getTimeFormatOptions: timeFormats.getTimeFormatOptions,
    guestTimeFormatKeys: timeFormats.guestTimeFormatKeys,
    toGuestPrefillConfig: guestPrefill.toGuestPrefillConfig,
    getThreadOptions: threads.getThreadOptions,
    withoutSent: drafts.withoutSent
  });
  const props = {
    guestDuration: { durationHours: 6, source: 'ui', envVarValue: 6 },
    onDurationChange: async () => undefined,
    guestDefaultsError: null,
    guestModeLocked: false,
    onToggleGuestLock: async (locked) => {
      lockCalls.push(locked);
    },
    guestCount: 0,
    onGuestsChanged: () => undefined,
    defaultGuestTheme: 'dark-default',
    onGuestThemeChange: async () => undefined,
    defaultGuestRefreshRate: 'STANDARD',
    onGuestRefreshRateChange: async () => undefined,
    guestRefreshRateLocked: false,
    onGuestRefreshRateLockChange: async () => undefined,
    availableThemes: []
  };

  /** The toggles, the Save button and any save failure as drawn now. */
  const draw = () => {
    cursor = 0;
    const toggles = {};
    let save = null;
    let failure = null;
    const walk = (node, rowLabel) => {
      if (Array.isArray(node)) {
        node.forEach((child) => walk(child, rowLabel));
        return;
      }
      if (!React.isValidElement(node)) return;
      const label = node.type === UserSettingRow ? node.props.label : rowLabel;
      if (node.type === ToggleSwitch) {
        const key = Object.keys(ROW_LABELS).find((name) => ROW_LABELS[name] === label);
        if (key) toggles[key] = node.props;
      }
      if (node.type === Button && node.props.children === 'common.save') save = node.props;
      // The confirmation dialogs draw warning alerts of their own.
      if (node.type === Alert && node.props.color === 'error') failure = node.props;
      Object.values(node.props).forEach((value) => walk(value, label));
    };
    walk(Pane(props), null);
    for (const key of Object.keys(ROW_LABELS)) assert.ok(toggles[key], `no ${key} toggle is drawn`);
    assert.ok(save, 'no Save button is drawn');
    return { toggles, save, failure };
  };
  return { draw, lockCalls };
};

/** Each request the pane sent, by the setting its URL names, answered when the test says. */
const holdRequests = () => {
  const requests = {};
  globalThis.fetch = (url, options) => {
    const key = String(url).split('/').pop();
    const answer = deferred();
    (requests[key] ??= []).push({ method: options?.method ?? 'GET', answer });
    return answer.promise;
  };
  return requests;
};

const ok = { ok: true, status: 200, json: async () => ({}) };

test('guest access: a change sends nothing until Save is clicked', async () => {
  const requests = holdRequests();
  try {
    const pane = mountPane();
    assert.equal(pane.draw().save.disabled, true, 'Save is enabled with nothing changed');

    pane.draw().toggles.sharpCorners.onChange('on');
    await settle();
    const changed = pane.draw();
    assert.deepEqual(requests, {}, 'the change was saved before Save was clicked');
    assert.equal(changed.toggles.sharpCorners.value, 'on', 'the change does not show');
    assert.equal(changed.save.disabled, false);

    changed.save.onClick();
    await settle();
    const saving = pane.draw();
    assert.equal(requests.sharpCorners?.length, 1);
    assert.equal(requests.sharpCorners[0].method, 'PATCH');
    assert.equal(saving.save.loading, true, 'Save does not show busy while it runs');
    for (const toggle of Object.values(saving.toggles))
      assert.notEqual(toggle.loading, true, 'a control shows busy while Save runs');

    requests.sharpCorners[0].answer.resolve(ok);
    await settle();
    const saved = pane.draw();
    assert.equal(saved.save.loading, false);
    assert.equal(saved.save.disabled, true, 'the saved change still counts as unsaved');
    assert.equal(saved.toggles.sharpCorners.value, 'on');
  } finally {
    delete globalThis.fetch;
  }
});

test('guest access: Save sends only the settings that changed, and a repeat click is ignored', async () => {
  const requests = holdRequests();
  try {
    const pane = mountPane();
    pane.draw().toggles.disableTooltips.onChange('off');
    pane.draw().toggles.signIn.onChange('off');
    // Changed and set back: no change left to send.
    pane.draw().toggles.showDatasourceLabels.onChange('off');
    pane.draw().toggles.showDatasourceLabels.onChange('on');

    pane.draw().save.onClick();
    pane.draw().save.onClick();
    await settle();
    assert.deepEqual(Object.keys(requests), ['disableTooltips']);
    assert.equal(requests.disableTooltips.length, 1, 'the repeat click sent the change again');
    assert.deepEqual(pane.lockCalls, [true], 'the sign-in lock was not stored once');
    requests.disableTooltips[0].answer.resolve(ok);
    await settle();
    assert.equal(pane.draw().save.disabled, true);
  } finally {
    delete globalThis.fetch;
  }
});

test('guest access: a refused setting keeps its change and names itself above Save', async () => {
  const requests = holdRequests();
  try {
    const pane = mountPane();
    pane.draw().toggles.sharpCorners.onChange('on');
    pane.draw().save.onClick();
    await settle();
    requests.sharpCorners[0].answer.resolve({ ok: false, status: 500 });
    await settle();

    const failed = pane.draw();
    assert.equal(failed.toggles.sharpCorners.value, 'on', 'the refused change was dropped');
    assert.equal(failed.save.disabled, false, 'Save cannot send the refused change again');
    assert.equal(failed.save.loading, false);
    assert.equal(failed.failure?.title, 'user.guest.errors.updateDefault');

    // The failure stays while the next Save runs, and a stored retry clears it.
    failed.save.onClick();
    await settle();
    assert.ok(pane.draw().failure, 'the failure was cleared at the click');
    requests.sharpCorners[1].answer.resolve(ok);
    await settle();
    assert.equal(pane.draw().failure, null);
  } finally {
    delete globalThis.fetch;
  }
});
