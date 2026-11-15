import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { LoadingSpinner } from './markup-shape.mjs';
import { compileToUrl, compileTree, make, moduleUrl, parseSource } from './transpile-module.mjs';

/**
 * A switch moves to the half the user clicked as soon as it is clicked, and stays there while its
 * save runs, even where the value it draws only moves once the server has answered. A failed save
 * puts it back. A guest's prefill access rows wait for their Save button instead: they send nothing
 * until Save, which alone shows busy, and a failed Save keeps the edits. A button that saves does
 * not fade while it saves, though a disabled button still does, and a saving button or switch keeps
 * its label in the page, so a screen reader still names it.
 *
 * The shipped switch, button and Session preferences pane run here on a small hook store that
 * keeps effect dependencies; the pane's other imports stand in as inert values.
 */

const OPTIONS = [
  { value: 'off', label: 'Off' },
  { value: 'on', label: 'On', activeColor: 'success' }
];

/** Whether a dependency list changed since the last render. */
const changed = (previous, next) =>
  !previous || !next || next.some((value, index) => !Object.is(value, previous[index]));

/** One mounted component's hooks; `render` draws it and runs the effects whose dependencies moved. */
const hookStore = () => {
  const slots = [];
  let cursor = 0;
  let effects = [];
  const hooks = {
    useState: (initial) => {
      const slot = (slots[cursor++] ??= {
        value: typeof initial === 'function' ? initial() : initial
      });
      return [
        slot.value,
        (next) => {
          slot.value = typeof next === 'function' ? next(slot.value) : next;
        }
      ];
    },
    useRef: (initial) => (slots[cursor++] ??= { current: initial }),
    useMemo: (build, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!('value' in slot) || changed(slot.deps, deps)) {
        slot.value = build();
        slot.deps = deps;
      }
      return slot.value;
    },
    useCallback: (callback, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.callback || changed(slot.deps, deps)) {
        slot.callback = callback;
        slot.deps = deps;
      }
      return slot.callback;
    },
    useEffect: (effect, deps) => {
      const slot = (slots[cursor++] ??= {});
      if (!slot.ran || changed(slot.deps, deps)) {
        slot.ran = true;
        slot.deps = deps;
        effects.push({ slot, effect });
      }
    },
    useId: () => {
      cursor++;
      return 'id';
    }
  };
  hooks.useLayoutEffect = hooks.useEffect;
  const render = (draw) => {
    cursor = 0;
    effects = [];
    const tree = draw();
    for (const { slot, effect } of effects) {
      if (typeof slot.cleanup === 'function') slot.cleanup();
      slot.cleanup = effect();
    }
    return tree;
  };
  return { hooks, render };
};

/** Every element in a drawn tree, children included. */
const elementsOf = (tree) => {
  const found = [];
  const walk = (node) => {
    if (Array.isArray(node)) node.forEach(walk);
    else if (React.isValidElement(node)) {
      found.push(node);
      Object.values(node.props).forEach(walk);
    }
  };
  walk(tree);
  return found;
};

/** The text written anywhere inside an element. */
const textOf = (node) =>
  typeof node === 'string'
    ? node
    : Array.isArray(node)
      ? node.map(textOf).join('')
      : React.isValidElement(node)
        ? textOf(node.props.children)
        : '';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

test('a switch whose value moves only when the server answers shows the clicked half while it saves', () => {
  const store = hookStore();
  const ToggleSwitch = make('src/components/ui/ToggleSwitch.tsx', 'ToggleSwitch', {
    React: { ...React, ...store.hooks },
    LoadingSpinner,
    Tooltip: stub('Tooltip')
  });
  const picked = [];
  const draw = (loading) =>
    store.render(() =>
      ToggleSwitch({
        options: OPTIONS,
        value: 'off',
        loading,
        onChange: (value) => picked.push(value),
        size: 'md'
      })
    );
  /** The half drawn active (the one wearing its color) and whether the spinner sits in it. */
  const shown = (tree) => {
    const halves = elementsOf(tree).filter(
      (element) => element.type === 'span' && element.props.style !== undefined
    );
    const active = halves.find((half) => Object.keys(half.props.style).length > 0);
    return {
      active: active ? textOf(active) : null,
      spinner: active
        ? elementsOf(active.props.children).some((element) => element.type === LoadingSpinner)
        : false
    };
  };

  draw(false).props.onClick();
  assert.deepEqual(picked, ['on'], 'clicking the Off switch does not ask for On');
  const saving = shown(draw(true));
  // The save failed: loading ends and the value never moved.
  const failed = shown(draw(false));

  assert.deepEqual(
    { saving, failed },
    { saving: { active: 'On', spinner: true }, failed: { active: 'Off', spinner: false } },
    'a switch clicked to On must draw On active, with the spinner on it, while its save runs, ' +
      'and Off again once a failed save ends'
  );
});

const SESSION_PREFERENCES = 'src/components/features/user/SessionPreferences.tsx';

/** Every name `path` imports. */
const importedNames = (path) =>
  parseSource(path, typescript.ScriptKind.TSX)
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

/**
 * An inert stand-in for an import the test does not bind: callable, any field of it is another
 * stand-in, it lists nothing and it is not a promise.
 */
const inert = () => {
  const fields = new Map();
  let result;
  return new Proxy(() => undefined, {
    get: (_target, key) => {
      if (key === Symbol.iterator) return () => [][Symbol.iterator]();
      if (key === Symbol.toPrimitive) return () => '';
      if (key === 'length') return 0;
      if (key === 'then' || key === '$$typeof') return undefined;
      if (!fields.has(key)) fields.set(key, inert());
      return fields.get(key);
    },
    apply: () => (result ??= inert()),
    construct: () => inert()
  });
};

const { DEFAULT_PREFERENCES } = await import(await compileToUrl('../src/types/userPreferences.ts'));
const { withoutSent } = await import(
  await compileToUrl('../src/components/features/user/draftEdits.ts')
);
const { PREFILL_SERVICES, prefillServiceRecord } = await import(
  await compileTree(
    '../src/components/features/prefill/hooks/prefillServiceConfig.ts',
    Object.fromEntries(
      ['SteamIcon', 'EpicIcon', 'BlizzardIcon', 'RiotIcon', 'XboxIcon'].map((name) => [
        `@components/ui/${name}`,
        moduleUrl(`export const ${name} = () => null;`)
      ])
    )
  )
);

const turn = () => new Promise((resolve) => setImmediate(resolve));

test("a guest's prefill access rows send nothing until Save, and a failed Save keeps the edits", async () => {
  const store = hookStore();
  const ToggleSwitch = stub('ToggleSwitch');
  const Button = stub('Button');
  const EnhancedDropdown = stub('EnhancedDropdown');
  const toggles = [];
  const puts = [];
  let stored = { ...DEFAULT_PREFERENCES, steamMaxThreadCount: 8 };
  let failNextPut = true;
  const held = () => new Promise(() => undefined);
  const api = new Proxy(
    {
      getSessionPreferences: async () => ({ ...stored }),
      saveSessionPreferences: async (_id, body) => {
        puts.push(body);
        if (failNextPut) {
          failNextPut = false;
          throw new Error('down');
        }
        stored = { ...body };
      },
      toggleGuestPrefillService: (id, service, enabled) =>
        new Promise((resolve, reject) => {
          toggles.push({
            id,
            service,
            enabled,
            succeed: resolve,
            fail: () => reject(new Error('refused'))
          });
        })
    },
    { get: (target, key) => (key in target ? target[key] : held) }
  );
  const Pane = make(SESSION_PREFERENCES, 'SessionPreferences', {
    ...Object.fromEntries(importedNames(SESSION_PREFERENCES).map((name) => [name, inert()])),
    React: { ...React, ...store.hooks },
    ...store.hooks,
    useTranslation: () => ({
      t: (key, values) => (values?.service ? `${key}:${values.service}` : key)
    }),
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useDefaultGuestPreferences: () => ({ prefs: { allowedTimeFormats: [] } }),
    getErrorMessage: (error) => error.message,
    DEFAULT_PREFERENCES,
    PREFILL_SERVICES,
    prefillServiceRecord,
    withoutSent,
    ToggleSwitch,
    Button,
    EnhancedDropdown,
    ApiService: api
  });
  const [steam] = PREFILL_SERVICES;
  let session = {
    id: 'guest-1',
    sessionType: 'guest',
    isCurrentSession: false,
    ...Object.fromEntries(PREFILL_SERVICES.map((service) => [service.sessionEnabledField, false]))
  };
  const props = () => ({
    session,
    availableThemes: [],
    defaultGuestTheme: 'dark-default',
    defaultGuestRefreshRate: 'STANDARD',
    guestDefaultsLoaded: true,
    guestThreadDefaults: {
      values: prefillServiceRecord(() => null),
      error: null,
      retrying: false,
      retry: async () => undefined
    }
  });
  const draw = () => elementsOf(store.render(() => Pane(props())));
  /** The pane's prefill access controls, as drawn now. */
  const controls = () => {
    const elements = draw();
    // The prefill section comes first in the pane, one switch per service.
    const switches = elements
      .filter((element) => element.type === ToggleSwitch)
      .slice(0, PREFILL_SERVICES.length)
      .map((element) => element.props);
    const steamRow = elements.find((element) => element.key === steam.id);
    const steamRowElements = steamRow ? elementsOf(steamRow.props.children) : [];
    return {
      switches,
      steamThreads: elements.find(
        (element) =>
          element.type === EnhancedDropdown &&
          element.props.triggerAriaLabel === `user.panes.prefs.threadsFor:${steam.displayName}`
      )?.props,
      steamUseDefault: steamRowElements.find(
        (element) => element.type === Button && textOf(element) === 'actions.useDefault'
      )?.props,
      save: elements.find((element) => element.type === Button && textOf(element) === 'common.save')
        ?.props,
      // The save failures the pane shows, by the title each one carries.
      errors: elements.filter(
        (element) => element.props?.title === 'activeSessions.errors.savePreferences'
      ).length
    };
  };
  const settle = async () => {
    for (let step = 0; step < 8; step += 1) {
      await turn();
      draw();
    }
  };
  const putsBeforeSave = () => puts.length;

  await settle();
  const first = controls();
  assert.equal(first.switches.length, PREFILL_SERVICES.length, 'the pane draws no access switches');
  assert.equal(first.steamThreads?.value, '8', 'the stored Steam thread limit is not drawn');

  // Use default on the stored override, then every service turned On: nothing is sent.
  first.steamUseDefault.onClick();
  await settle();
  for (const props of controls().switches) props.onChange('on');
  await settle();
  const edited = controls();
  assert.deepEqual(
    {
      toggles: toggles.length,
      puts: putsBeforeSave(),
      switches: edited.switches.map((props) => props.value),
      steamThreads: edited.steamThreads.value,
      useDefaultShown: edited.steamUseDefault !== undefined,
      saveDisabled: edited.save?.disabled
    },
    {
      toggles: 0,
      puts: 0,
      switches: PREFILL_SERVICES.map(() => 'on'),
      steamThreads: '',
      useDefaultShown: false,
      saveDisabled: false
    },
    'a switch or thread limit change must only change what the rows draw until Save is pressed'
  );

  // Save sends every changed row; only Save shows busy. The thread write fails, and so does Steam's
  // grant, while the other grants' pushes land before their replies.
  edited.save.onClick();
  await settle();
  const saving = controls();
  const sent = toggles.map(({ id, service, enabled }) => ({ id, service, enabled }));
  const firstPut = puts[0];
  for (const toggle of toggles.slice(1)) {
    const service = PREFILL_SERVICES.find((candidate) => candidate.id === toggle.service);
    session = { ...session, [service.sessionEnabledField]: true };
  }
  await settle();
  toggles.shift().fail();
  for (const toggle of toggles.splice(0)) toggle.succeed();
  await settle();
  const failed = controls();
  assert.deepEqual(
    {
      sent,
      putSteamThreads: firstPut?.steamMaxThreadCount,
      saveBusy: saving.save.loading,
      switchesBusy: saving.switches.map((props) => props.loading === true),
      errors: failed.errors,
      switches: failed.switches.map((props) => props.value),
      steamThreads: failed.steamThreads.value,
      saveBusyAfter: failed.save.loading,
      saveDisabledAfter: failed.save.disabled
    },
    {
      sent: PREFILL_SERVICES.map((service) => ({
        id: 'guest-1',
        service: service.id,
        enabled: true
      })),
      putSteamThreads: null,
      saveBusy: true,
      switchesBusy: PREFILL_SERVICES.map(() => false),
      errors: 1,
      switches: PREFILL_SERVICES.map(() => 'on'),
      steamThreads: '',
      saveBusyAfter: false,
      saveDisabledAfter: false
    },
    'Save must send each changed row once, show busy on itself only, and after a failure keep the ' +
      'edits on screen with one error'
  );

  // Saving again: Steam's push lands before its reply, then a revoke from elsewhere turns it Off.
  failed.save.onClick();
  await settle();
  const retried = toggles.map(({ service, enabled }) => ({ service, enabled }));
  session = { ...session, [steam.sessionEnabledField]: true };
  await settle();
  toggles.shift().succeed();
  await settle();
  const saved = controls();
  session = { ...session, [steam.sessionEnabledField]: false };
  await settle();
  const revoked = controls();
  assert.deepEqual(
    {
      retried,
      secondPutSteamThreads: puts[1]?.steamMaxThreadCount,
      steamAfterSave: saved.switches[0].value,
      saveDisabledAfterSave: saved.save.disabled,
      steamAfterRevoke: revoked.switches[0].value
    },
    {
      retried: [{ service: steam.id, enabled: true }],
      secondPutSteamThreads: null,
      steamAfterSave: 'on',
      saveDisabledAfterSave: true,
      steamAfterRevoke: 'off'
    },
    'a saved grant must show On once saved and follow a later revoke to Off'
  );
});

test('a saving button does not fade, a disabled or permission-checking one still does, and both keep their label', () => {
  const store = hookStore();
  const Button = make('src/components/ui/Button.tsx', 'Button', {
    React,
    LoadingSpinner,
    useOptionalDirectoryPermissionsContext: () => null
  });
  const ToggleSwitch = make('src/components/ui/ToggleSwitch.tsx', 'ToggleSwitch', {
    React: { ...React, ...store.hooks },
    LoadingSpinner,
    Tooltip: stub('Tooltip')
  });
  const buttonMarkup = (props) =>
    renderToStaticMarkup(
      React.createElement(Button, { onClick: () => undefined, ...props }, 'Save')
    );
  const fades = (markup) => /disabled:opacity-/.test(markup);
  const saving = buttonMarkup({ loading: true });
  const busySwitch = renderToStaticMarkup(
    store.render(() =>
      ToggleSwitch({ options: OPTIONS, value: 'on', loading: true, onChange: () => undefined })
    )
  );

  assert.deepEqual(
    {
      savingFades: fades(saving),
      disabledFades: fades(buttonMarkup({ disabled: true })),
      checkingFades: fades(buttonMarkup({ checkingPermissions: true })),
      savingHidesLabel: /\binvisible\b/.test(saving),
      switchHidesLabel: /\binvisible\b/.test(busySwitch),
      switchFades: /opacity-60/.test(busySwitch)
    },
    {
      savingFades: false,
      disabledFades: true,
      checkingFades: true,
      savingHidesLabel: false,
      switchHidesLabel: false,
      switchFades: false
    },
    `a saving control fades or drops its label from the page: button ${saving}; switch ${busySwitch}`
  );
});
