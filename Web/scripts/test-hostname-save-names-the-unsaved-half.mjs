import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import {
  bindLifted,
  liftHookCallback,
  make,
  parseSource,
  translator
} from './transpile-module.mjs';

/**
 * The Clients page saves client-name lookup in two writes: the lookup settings, then the lookup
 * switch. When mock mode turns on in Settings after the settings reached the server but before the
 * switch write was sent, the server holds the new settings and the old switch. The page must then say
 * which part was not saved, in both shipped languages, and not the plain "Failed to save hostname
 * settings", which reads as if nothing was written. Mock mode is turned on at each point of the save in
 * turn. The shipped page Save runs against the shipped hostname provider on a hook store, with every
 * server call held until the test answers it.
 */

const CLIENTS = 'src/components/features/management/sections/ClientsSection.tsx';
const HOSTNAMES = 'src/contexts/ClientHostnameContext.tsx';
const WHOLE_SAVE_FAILED = 'management.sections.clients.hostnames.saveFailed';
const LOCALES = ['en', 'zh'].map((locale) => [locale, translator(locale)]);
const SETTINGS = {
  resolver: '192.168.1.1',
  guestAccess: false,
  routerLookup: true,
  dockerLookup: true
};

/** Draws nothing; the provider's children are not under test. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

const importedNames = parseSource(HOSTNAMES, typescript.ScriptKind.TSX)
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

/** A call the test answers by hand. */
const heldCall = (list, name) => (argument) =>
  new Promise((resolve) => list.push({ name, argument, resolve }));

const mountHostnames = () => {
  const reads = [];
  const writes = [];
  const sent = [];
  const mock = { mockMode: false };
  const { hooks, draw } = hookStore();
  const Hostnames = make(HOSTNAMES, 'ClientHostnameProvider', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    ApiService: {
      getClientHostnames: heldCall(reads, 'read'),
      setClientHostnameLookup: (enabled) => {
        sent.push('lookup switch');
        return heldCall(writes, 'lookup switch')(enabled);
      },
      setClientHostnameSettings: (settings) => {
        sent.push('lookup settings');
        return heldCall(writes, 'lookup settings')(settings);
      }
    },
    useAuth: () => ({ authMode: 'authenticated', isLoading: false }),
    useMockMode: () => mock,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: false }),
    useReconnectRefetch: () => undefined,
    getErrorMessage: (error) => error.message,
    ClientHostnameContext: { Provider: stub('ClientHostnameContext') }
  });
  const shown = () => draw(() => Hostnames({ children: null })).props.value;
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      shown();
    }
  };
  return { shown, settle, reads, writes, sent, mock };
};

/** Answers every call still out in `list`, a write with what the server would store. */
const answerAll = (list) => {
  for (const call of list.splice(0)) {
    if (call.name === 'read')
      call.resolve({ enabled: false, hostnames: {}, reason: 'none', settings: SETTINGS });
    else if (call.name === 'lookup switch') call.resolve({ enabled: call.argument });
    else call.resolve(call.argument);
  }
};

/** Whether `key` names a string in every shipped locale. */
const translatedEverywhere = (key) =>
  LOCALES.every(([, translate]) => {
    try {
      return translate(key).length > 0;
    } catch {
      return false;
    }
  });

const MOCK_ON_POINTS = [
  'while the lookup settings write is out',
  'while the read after the settings write is out',
  'while the lookup switch write is out',
  'after the save finished'
];

test('a hostname save cut short by mock mode says which part was not saved', async () => {
  const wrong = [];
  let halfSaved = 0;
  for (const point of MOCK_ON_POINTS) {
    const page = mountHostnames();
    await page.settle();
    answerAll(page.reads);
    await page.settle();

    const errors = [];
    const shownAtClick = page.shown();
    const save = bindLifted(liftHookCallback(CLIENTS, 'useCallback', 'hostnames.settingsSaved'), {
      mockMode: false,
      savingHostnamesRef: { current: false },
      setSavingHostnames: () => undefined,
      setHostnameSettings: shownAtClick.setSettings,
      setHostnamesEnabled: shownAtClick.setEnabled,
      // The admin changed the resolver and turned the lookup on.
      hostnameForm: { ...SETTINGS, enabled: true },
      hostnamesEnabled: false,
      onSuccess: () => undefined,
      onError: (message) => errors.push(message),
      t: (key) => key
    });

    const mockOn = async (at) => {
      if (at !== point) return;
      page.mock.mockMode = true;
      await page.settle();
    };
    const saving = save();
    await page.settle();
    await mockOn(MOCK_ON_POINTS[0]);
    answerAll(page.writes);
    await page.settle();
    await mockOn(MOCK_ON_POINTS[1]);
    answerAll(page.reads);
    await page.settle();
    await mockOn(MOCK_ON_POINTS[2]);
    answerAll(page.writes);
    await page.settle();
    answerAll(page.reads);
    await saving;
    await page.settle();
    await mockOn(MOCK_ON_POINTS[3]);

    // Only a save whose settings reached the server and whose switch did not is half saved.
    if (!page.sent.includes('lookup settings') || page.sent.includes('lookup switch')) continue;
    halfSaved += 1;
    if (errors.length === 0) {
      wrong.push(
        `mock mode turned on ${point}: the page reports nothing about the half-saved form`
      );
      continue;
    }
    for (const message of errors) {
      if (message === WHOLE_SAVE_FAILED)
        wrong.push(
          `mock mode turned on ${point}: the page says "${translator('en')(WHOLE_SAVE_FAILED)}" although the lookup settings were saved and only the lookup switch was not`
        );
      else if (!translatedEverywhere(message))
        wrong.push(
          `mock mode turned on ${point}: the page shows "${message}", which en and zh do not both translate`
        );
    }
  }
  assert.ok(
    halfSaved > 0,
    'no mock-mode point left the form half saved, so the test proves nothing'
  );
  assert.deepEqual(wrong, [], `the hostname save names the wrong outcome: ${wrong.join('; ')}`);
});
