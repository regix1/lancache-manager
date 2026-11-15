import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { make, parseSource } from './transpile-module.mjs';

/**
 * With sign-in off every browser runs on one shared session, and the next request of any browser
 * makes it again, so the server refuses to revoke or delete it (`SessionsController.cs`). Its row
 * in the Users page detail must offer neither Revoke nor Delete session, and no session offers Log
 * Out while sign-in is off. Another live session that is not this browser's keeps Revoke and Delete
 * session, and an ended shared row keeps its history Delete.
 *
 * The shipped detail runs on the small hook store of
 * `test-session-detail-log-out-with-authentication-off.mjs`; every component it draws is a stub,
 * and the test reads the buttons and the menu items the header is drawn with.
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
const ActionMenuItem = stub('ActionMenuItem');
const RowActionsMenu = stub('RowActionsMenu');

/** A live admin session that is not this browser's. */
const otherSession = {
  id: 'other-session',
  sessionType: 'admin',
  username: null,
  accountDeleted: false,
  ipAddress: '192.168.1.20',
  userAgent: null,
  createdAt: '2026-11-14T08:00:00Z',
  lastSeenAt: '2026-11-14T09:00:00Z',
  expiresAt: '2099-12-31T00:00:00Z',
  isExpired: false,
  isRevoked: false,
  revokedAt: null,
  isCurrentSession: false,
  isSharedSession: false,
  prefillEnabled: true,
  steamPrefillEnabled: true,
  epicPrefillEnabled: true,
  battlenetPrefillEnabled: true,
  riotPrefillEnabled: true,
  xboxPrefillEnabled: true,
  signedOut: false
};

/**
 * The labels of every button and header menu item the detail draws for a session.
 *
 * @param {object} session The session the detail shows.
 * @returns {string[]} The translation key of each control.
 */
const drawnControls = (session) => {
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
    ActionMenuItem,
    RowActionsMenu,
    useTranslation: () => ({ t: (key) => key }),
    useAuth: () => ({
      authenticationEnabled: false,
      accountId: 'owner-account',
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
    sessionName: () => 'Shared access',
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
    if (node.type === Button || node.type === ActionMenuItem) labels.push(node.props.children);
    if (node.type === RowActionsMenu) walk(node.props.children(() => undefined));
    Object.values(node.props).forEach(walk);
  };
  cursor = 0;
  walk(
    Detail({
      session,
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

test('the shared session offers no Revoke and no Delete session while sign-in is off', () => {
  const shared = drawnControls({ ...otherSession, id: 'shared-session', isSharedSession: true });
  assert.equal(
    shared.includes('activeSessions.actions.revoke'),
    false,
    `the shared session's detail draws Revoke (controls: ${shared.join(', ')}), and the next request only makes it again`
  );
  assert.equal(
    shared.includes('user.panes.menu.deleteSession'),
    false,
    `the shared session's detail draws Delete session (controls: ${shared.join(', ')}), and the next request only makes it again`
  );
});

test('no session draws Log Out while sign-in is off', () => {
  // Turning sign-in off deletes every account, so no session but the shared one can sign out, and
  // the server keeps that one on sign-out.
  const own = drawnControls({ ...otherSession, id: 'own-session', isCurrentSession: true });
  assert.equal(
    own.includes('activeSessions.actions.logout'),
    false,
    `with sign-in off, this browser's own session draws Log Out (controls: ${own.join(', ')})`
  );

  const shared = drawnControls({
    ...otherSession,
    id: 'shared-session',
    isCurrentSession: true,
    isSharedSession: true
  });
  assert.equal(
    shared.includes('activeSessions.actions.logout'),
    false,
    `with sign-in off, the shared session draws Log Out (controls: ${shared.join(', ')}), whose click keeps the shared session`
  );
});

test('another live session keeps Revoke and Delete session, and an ended shared row keeps Delete', () => {
  const other = drawnControls(otherSession);
  assert.ok(
    other.includes('activeSessions.actions.revoke'),
    `a live session that is not this browser's lost Revoke (controls: ${other.join(', ')})`
  );
  assert.ok(
    other.includes('user.panes.menu.deleteSession'),
    `a live session that is not this browser's lost Delete session (controls: ${other.join(', ')})`
  );

  const ended = drawnControls({
    ...otherSession,
    id: 'old-shared-session',
    isRevoked: true,
    revokedAt: '2026-11-14T09:30:00Z'
  });
  assert.ok(
    ended.includes('activeSessions.actions.delete'),
    `an ended shared row lost its history Delete (controls: ${ended.join(', ')})`
  );
});
