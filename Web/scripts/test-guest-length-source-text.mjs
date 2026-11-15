import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { compileTree, make, moduleUrl, parseSource } from './transpile-module.mjs';

/**
 * The Guest access pane's Session length row says, in its help, where the length comes from: set on
 * this page, set in the config file, or the built-in default. The server's answer names the origin
 * in `source`, so the help shown must follow `source`: a length from the config file must not read
 * "Uses the built-in default" because the config file happens to name the same hours.
 *
 * The whole shipped pane runs here on a small hook store with a translator that reads the shipped
 * en.json, and the test reads the text drawn in the Session length row's help.
 */

const COMPONENT = 'src/components/features/user/GuestConfiguration.tsx';

const english = JSON.parse(
  readFileSync(new URL('../src/i18n/locales/en.json', import.meta.url), 'utf8')
);
const translate = (key) => {
  const text = key.split('.').reduce((node, part) => node?.[part], english);
  return typeof text === 'string' ? text : `no en text for ${key}`;
};

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the pane imports, so a stub stands in for any the test does not bind itself. */
const importedNames = parseSource(COMPONENT)
  .statements.filter(typescript.isImportDeclaration)
  .flatMap((declaration) => {
    const clause = declaration.importClause;
    if (!clause || clause.isTypeOnly) return [];
    const names = clause.name ? [clause.name.text] : [];
    const bindings = clause.namedBindings;
    if (bindings && typescript.isNamedImports(bindings))
      names.push(
        ...bindings.elements
          .filter((element) => !element.isTypeOnly)
          .map((element) => element.name.text)
      );
    return names;
  });

/** The service icons are drawings; the pane only passes them on. */
const serviceIcons = Object.fromEntries(
  ['SteamIcon', 'EpicIcon', 'BlizzardIcon', 'RiotIcon', 'XboxIcon'].map((name) => [
    `@components/ui/${name}`,
    moduleUrl(`export const ${name} = () => null;`)
  ])
);

const [onOff, threads, timeFormats, userTypes, guestPrefill, prefillServices, timezone, pending] =
  await Promise.all(
    [
      '../src/components/features/user/onOffOptions.ts',
      '../src/components/features/user/threadOptions.ts',
      '../src/components/features/user/timeFormatOptions.ts',
      '../src/components/features/user/types.ts',
      '../src/components/features/user/guestPrefillConfig.ts',
      '../src/components/features/prefill/hooks/prefillServiceConfig.ts',
      '../src/contexts/TimezoneContext.types.ts',
      '../src/utils/pendingPreferences.ts'
    ].map(async (path) => import(await compileTree(path, serviceIcons)))
  );

const UserSettingRow = stub('UserSettingRow');

/** The text drawn in the Session length row's help when the server answers `guestDuration`. */
const sessionLengthHelp = (guestDuration) => {
  const slots = [];
  let cursor = 0;
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
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    useTranslation: () => ({ t: translate }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useReconnectRefetch: () => undefined,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useAuth: () => ({ authMode: 'authenticated', isAdmin: true }),
    UserSettingRow,
    ApiService: {
      getFetchOptions: (options) => ({ ...options }),
      getJsonFetchOptions: (body, options) => ({ ...options, body: JSON.stringify(body) })
    },
    assertOk: async (response) => response,
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
    getThreadOptions: threads.getThreadOptions
  });
  const props = {
    guestDuration,
    onDurationChange: async () => undefined,
    updatingDuration: false,
    guestDefaultsError: null,
    guestModeLocked: false,
    updatingGuestLock: false,
    onToggleGuestLock: async () => undefined,
    guestCount: 0,
    onGuestsChanged: () => undefined,
    defaultGuestTheme: 'dark-default',
    onGuestThemeChange: () => undefined,
    updatingGuestTheme: false,
    defaultGuestRefreshRate: 'STANDARD',
    onGuestRefreshRateChange: () => undefined,
    updatingGuestRefreshRate: false,
    guestRefreshRateLocked: false,
    onGuestRefreshRateLockChange: () => undefined,
    updatingGuestRefreshRateLock: false,
    availableThemes: []
  };

  const rows = [];
  const findRows = (node) => {
    if (Array.isArray(node)) {
      node.forEach(findRows);
      return;
    }
    if (!React.isValidElement(node)) return;
    if (
      node.type === UserSettingRow &&
      node.props.label === translate('user.panes.prefs.sessionLength')
    )
      rows.push(node.props);
    Object.values(node.props).forEach(findRows);
  };
  findRows(Pane(props));
  assert.equal(rows.length, 1, 'the pane does not draw one Session length row');

  const texts = [];
  const collectText = (node) => {
    if (Array.isArray(node)) node.forEach(collectText);
    else if (typeof node === 'string') texts.push(node);
    else if (React.isValidElement(node)) Object.values(node.props).forEach(collectText);
  };
  collectText(rows[0].help);
  return texts.join(' ');
};

const ORIGINS = [
  {
    name: 'a length from the config file',
    answer: { durationHours: 12, source: 'config', canEdit: true, envVarValue: 12 },
    shows: /config file/i,
    neverShows: /built-in default|set on this page/i
  },
  {
    name: 'a length the config file sets to the built-in hours',
    answer: { durationHours: 6, source: 'config', canEdit: true, envVarValue: 6 },
    shows: /config file/i,
    neverShows: /built-in default|set on this page/i
  },
  {
    name: 'the built-in default',
    answer: { durationHours: 6, source: 'default', canEdit: true, envVarValue: 6 },
    shows: /built-in default/i,
    neverShows: /comes from the config file|set on this page/i
  },
  {
    name: 'a length set on this page',
    answer: { durationHours: 12, source: 'ui', canEdit: true, envVarValue: 6 },
    shows: /set on this page/i,
    neverShows: /built-in default|comes from the config file/i
  }
];

for (const origin of ORIGINS) {
  test(`the Session length help names where ${origin.name} comes from`, () => {
    const help = sessionLengthHelp(origin.answer);
    assert.doesNotMatch(help, /no en text for/, help);
    assert.match(
      help,
      origin.shows,
      `for ${origin.name} (source "${origin.answer.source}") the Session length help reads "${help}"`
    );
    assert.doesNotMatch(
      help,
      origin.neverShows,
      `for ${origin.name} (source "${origin.answer.source}") the Session length help reads "${help}"`
    );
  });
}
