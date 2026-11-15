import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * With authentication off, every browser shares one admin session, and Log Out ends nothing: the
 * server keeps the shared session (`AuthController.cs:723-726`), the provider keeps the window
 * signed in (`AuthContext.tsx:173`), and the status read that follows reports the same session. So
 * a Log Out button on this browser's session in the Users page detail is a click that changes
 * nothing and says nothing. Settings hides its Log Out when authentication is off
 * (`AuthenticationManager.tsx:301-313`); the Users page detail must do the same, and keep the
 * button when authentication is on.
 *
 * The whole shipped detail runs here on a small hook store, following the one in
 * `test-sign-in-wizard-closes-with-fade.mjs`; every component it draws is a stub, and the test
 * reads the buttons the header is drawn with.
 */

const COMPONENT = 'src/components/features/user/SessionDetail.tsx';

/** Draws nothing; the test reads the props it was given from the element tree. */
const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

/** Every name the detail imports, so a stub stands in for any the test does not bind itself. */
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

const Button = stub('Button');

/** This browser's own admin session, live. */
const currentSession = {
  id: 'shared-session',
  sessionType: 'admin',
  username: null,
  accountDeleted: false,
  ipAddress: '192.168.1.20',
  userAgent: null,
  createdAt: '2026-11-14T08:00:00Z',
  lastSeenAt: '2026-11-14T09:00:00Z',
  expiresAt: '2026-12-14T08:00:00Z',
  isExpired: false,
  isRevoked: false,
  revokedAt: null,
  isCurrentSession: true,
  prefillEnabled: false,
  steamPrefillEnabled: false,
  epicPrefillEnabled: false,
  battlenetPrefillEnabled: false,
  riotPrefillEnabled: false,
  xboxPrefillEnabled: false,
  signedOut: false
};

/**
 * The button labels the detail draws for this browser's session.
 *
 * @param {boolean} authenticationEnabled False for an install running with authentication off.
 * @returns {string[]} The translation key of every button the detail draws.
 */
const drawnButtonLabels = (authenticationEnabled) => {
  const slots = [];
  let cursor = 0;
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
    useEffect: () => {
      cursor++;
    }
  };
  const Detail = make(COMPONENT, 'SessionDetail', {
    ...Object.fromEntries(importedNames.map((name) => [name, stub(name)])),
    React: { ...React, ...hooks },
    ...hooks,
    Button,
    useTranslation: () => ({ t: (key) => key }),
    useAuth: () => ({
      authenticationEnabled,
      accountId: null,
      isMainAdmin: true,
      refreshAuth: async () => undefined,
      logout: async () => true
    }),
    useErrorHandler: () => ({ notifyError: () => undefined }),
    useSessionPreferences: () => ({
      getSessionPreferences: () => null,
      loadSessionPreferences: async () => undefined,
      isLoaded: () => true,
      isLoading: () => false
    }),
    useCopyFeedback: () => [false, () => undefined],
    useFormattedDateTime: (value) => value ?? '',
    isAccountHolder: (sessionType) => sessionType === 'admin' || sessionType === 'user',
    formatLocation: () => null,
    deviceTitle: () => 'device',
    sessionName: () => 'This browser',
    cleanIpAddress: (ip) => ip,
    formatLastSeen: () => 'now',
    formatTimeLeft: () => 'later'
  });

  const labels = [];
  const walk = (node) => {
    if (Array.isArray(node)) {
      node.forEach(walk);
      return;
    }
    if (!React.isValidElement(node)) return;
    if (node.type === Button) labels.push(node.props.children);
    Object.values(node.props).forEach(walk);
  };
  cursor = 0;
  walk(
    Detail({
      session: currentSession,
      account: null,
      otherSessions: [],
      availableThemes: [],
      defaultGuestTheme: 'dark-default',
      defaultGuestRefreshRate: 'STANDARD',
      guestDefaultsLoaded: true,
      guestDefaultsError: null,
      onRetryGuestDefaults: async () => undefined,
      accountActions: {
        onEdit: () => undefined,
        onSetDisabled: () => undefined,
        onDelete: () => undefined,
        disablingAccountId: null
      },
      getSessionStatus: () => 'online',
      onPickSession: () => undefined,
      onRevoke: () => undefined,
      onDelete: () => undefined
    })
  );
  return labels;
};

test('the Users page detail offers Log Out on this browser session only when authentication is on', () => {
  assert.ok(
    drawnButtonLabels(true).includes('activeSessions.actions.logout'),
    'with authentication on, the detail of this browser session draws no Log Out button'
  );

  const withAuthenticationOff = drawnButtonLabels(false);
  assert.equal(
    withAuthenticationOff.includes('activeSessions.actions.logout'),
    false,
    `with authentication off, the detail of this browser session draws a Log Out button (buttons: ${withAuthenticationOff.join(', ')}) whose click keeps the shared session and shows nothing`
  );
});
