import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { bindLifted, liftHookCallback, make, parseSource } from './transpile-module.mjs';

/**
 * The Clients page saves client-name lookup in two writes: the lookup settings, then the lookup
 * switch. When mock mode turns on in Settings while that save runs, the page must not say the form
 * was saved unless both writes reached the server; otherwise it shows the save as failed. Mock mode is
 * turned on at each point of the save in turn. The shipped page Save runs against the shipped
 * hostname provider on a hook store, with every server call held until the test answers it.
 */

const CLIENTS = 'src/components/features/management/sections/ClientsSection.tsx';
const HOSTNAMES = 'src/contexts/ClientHostnameContext.tsx';
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

const MOCK_ON_POINTS = [
  'while the lookup settings write is out',
  'while the read after the settings write is out',
  'while the lookup switch write is out',
  'after the save finished'
];

test('a hostname save cut short by mock mode never says the whole form was saved', async () => {
  const wrong = [];
  for (const point of MOCK_ON_POINTS) {
    const page = mountHostnames();
    await page.settle();
    answerAll(page.reads);
    await page.settle();

    const toasts = [];
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
      onSuccess: (message) => toasts.push({ kind: 'success', message }),
      onError: (message) => toasts.push({ kind: 'error', message }),
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

    const bothSent = page.sent.includes('lookup settings') && page.sent.includes('lookup switch');
    const saidSaved = toasts.some((toast) => toast.kind === 'success');
    const saidFailed = toasts.some((toast) => toast.kind === 'error');
    if (saidSaved && !bothSent)
      wrong.push(
        `mock mode turned on ${point}: the page says "${toasts.find((toast) => toast.kind === 'success').message}" while the server got only ${JSON.stringify(page.sent)}`
      );
    else if (!saidSaved && !saidFailed)
      wrong.push(`mock mode turned on ${point}: the page reports nothing about the save`);
  }
  assert.deepEqual(wrong, [], `the hostname save reports the wrong outcome: ${wrong.join('; ')}`);
});
