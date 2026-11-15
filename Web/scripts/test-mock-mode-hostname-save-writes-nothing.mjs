import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { hookStore, turn } from './component-hooks.mjs';
import { make, parseSource } from './transpile-module.mjs';

/**
 * Mock mode shows generated data, so nothing done on a mock page reaches the real server. The Clients
 * hostname form copies the settings the provider hands it into every field the admin has not edited
 * (`Web/src/components/features/management/sections/ClientsSection.tsx:578-597`) and its Save sends all
 * four fields (`:615-620`). So with mock mode on, editing one field and clicking Save must leave the
 * real saved settings exactly as they were. The shipped provider runs on a hook store over a fake
 * server that holds real settings.
 */

const HOSTNAMES = 'src/contexts/ClientHostnameContext.tsx';
const SAVED = {
  resolver: '192.168.1.1',
  guestAccess: true,
  routerLookup: false,
  dockerLookup: false
};

/** Draws nothing; the test reads the value the provider hands its children. */
const inert = (name) => {
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

test('a hostname settings save made in mock mode leaves the real saved settings as they were', async () => {
  const server = { settings: { ...SAVED }, writes: [] };
  const mock = { mockMode: false };
  const { hooks, draw } = hookStore();
  const Hostnames = make(HOSTNAMES, 'ClientHostnameProvider', {
    ...Object.fromEntries(importedNames.map((name) => [name, inert(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    ApiService: {
      getClientHostnames: async () => ({
        enabled: true,
        hostnames: {},
        reason: 'none',
        settings: { ...server.settings }
      }),
      setClientHostnameSettings: async (settings) => {
        server.writes.push(settings);
        server.settings = { ...settings };
        return { ...server.settings };
      },
      setClientHostnameLookup: async (enabled) => ({ enabled })
    },
    useAuth: () => ({ authMode: 'authenticated', isLoading: false }),
    useMockMode: () => mock,
    useSignalR: () => ({ on: () => undefined, off: () => undefined, isConnected: true }),
    useReconnectRefetch: () => undefined,
    getErrorMessage: (error) => error.message,
    ClientHostnameContext: { Provider: inert('ClientHostnameContext') }
  });
  const shown = () => draw(() => Hostnames({ children: null })).props.value;
  const settle = async () => {
    for (let pass = 0; pass < 6; pass += 1) {
      await turn();
      shown();
    }
  };

  await settle();
  assert.deepEqual(shown().settings, SAVED, 'the form never showed the real saved settings');

  // The admin turns mock mode on in Settings, then edits the resolver on the Clients form and saves.
  mock.mockMode = true;
  await settle();
  const form = { ...shown().settings, resolver: '10.0.0.53' };
  let failure = null;
  try {
    await shown().setSettings(form);
  } catch (error) {
    failure = error.message;
  }
  await settle();

  assert.deepEqual(
    server.writes,
    [],
    `the mock-mode Save wrote to the real server: it sent ${JSON.stringify(server.writes)}, so the real settings ${JSON.stringify(SAVED)} became ${JSON.stringify(server.settings)}${failure === null ? '' : ` (the save also failed: ${failure})`}`
  );
});
