import assert from 'node:assert/strict';
import test from 'node:test';
import typescript from 'typescript';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { make, parseSource } from './transpile-module.mjs';

/**
 * A user-role account changes nobody but itself, and the server refuses the rest. So the Users page
 * offers it no action and no preference rows on another account's session or account, and keeps
 * them on its own. An admin keeps them everywhere.
 *
 * The session detail runs on the small hook store of `test-shared-session-has-no-revoke.mjs`: every
 * component it draws is a stub, and the test reads the controls and components the pane is drawn
 * with. The account detail is rendered to markup with its menu items drawn in place.
 */

const USER_FOLDER = 'src/components/features/user/';
const SESSION_DETAIL = `${USER_FOLDER}SessionDetail.tsx`;

const stub = (name) => {
  const component = () => null;
  Object.defineProperty(component, 'name', { value: name });
  return component;
};

const importedNames = parseSource(SESSION_DETAIL)
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

const stubs = Object.fromEntries(importedNames.map((name) => [name, stub(name)]));

const otherAccount = {
  id: 'other-account',
  username: 'jon',
  role: 'user',
  isMainAdmin: false,
  isDisabled: false,
  createdAtUtc: '2026-10-01T00:00:00Z',
  lastLoginAtUtc: '2026-11-14T08:00:00Z'
};

const sessionOf = (accountId, overrides = {}) => ({
  id: `${accountId}-session`,
  sessionType: 'user',
  accountId,
  username: 'jon',
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
  prefillEnabled: false,
  steamPrefillEnabled: false,
  epicPrefillEnabled: false,
  battlenetPrefillEnabled: false,
  riotPrefillEnabled: false,
  xboxPrefillEnabled: false,
  signedOut: false,
  ...overrides
});

/**
 * What the session detail draws for a viewer.
 *
 * @returns {{ controls: string[], components: string[] }} Each button and menu item's label key, and
 *   the name of every stubbed component in the tree.
 */
const drawSessionDetail = (session, account, viewer) => {
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
  const Button = stub('Button');
  const ActionMenuItem = stub('ActionMenuItem');
  const RowActionsMenu = stub('RowActionsMenu');
  const Detail = make(SESSION_DETAIL, 'SessionDetail', {
    ...stubs,
    React: { ...React, ...hooks },
    ...hooks,
    Button,
    ActionMenuItem,
    RowActionsMenu,
    useTranslation: () => ({ t: (key) => key }),
    useAuth: () => ({
      authenticationEnabled: true,
      refreshAuth: async () => undefined,
      logout: async () => true,
      ...viewer
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
    sessionName: () => 'jon',
    cleanIpAddress: (ip) => ip,
    formatLastSeen: () => 'now',
    formatTimeLeft: () => 'later'
  });

  const controls = [];
  const components = [];
  const walk = (node) => {
    if (Array.isArray(node)) {
      node.forEach(walk);
      return;
    }
    if (!React.isValidElement(node)) return;
    // Copying the session id changes nothing, so it is not one of the controls under test.
    if (
      (node.type === Button || node.type === ActionMenuItem) &&
      node.props['aria-label'] !== 'user.panes.facts.copyAria'
    )
      controls.push(node.props.children);
    if (typeof node.type === 'function' && node.type.name) components.push(node.type.name);
    if (node.type === RowActionsMenu) walk(node.props.children(() => undefined));
    Object.values(node.props).forEach(walk);
  };
  walk(
    Detail({
      session,
      account,
      otherSessions: [],
      availableThemes: [],
      defaultGuestTheme: 'dark-default',
      defaultGuestRefreshRate: 'STANDARD',
      guestDefaultsLoaded: true,
      guestDefaultsError: 'failed',
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
  return { controls, components };
};

const renderAccountDetail = (account, viewer) => {
  const UserPaneHeader = make(`${USER_FOLDER}UserPaneHeader.tsx`, 'UserPaneHeader', { React });
  const AccountDetail = make(`${USER_FOLDER}AccountDetail.tsx`, 'AccountDetail', {
    React,
    useState: React.useState,
    useMemo: React.useMemo,
    useTranslation: () => ({ t: (key) => key }),
    useAuth: () => viewer,
    useReaderClock: () => ({}),
    formatTimestamp: () => 'created',
    formatLastSeen: () => 'last seen',
    UserPaneHeader,
    Button: ({ children }) => React.createElement('button', null, children),
    RowActionsMenu: ({ children }) =>
      React.createElement(
        'menu',
        null,
        children(() => undefined)
      ),
    ActionMenuItem: ({ children }) => React.createElement('li', null, children),
    ActionMenuDivider: () => null
  });
  return renderToStaticMarkup(
    React.createElement(AccountDetail, {
      account,
      accountActions: {
        onEdit: () => undefined,
        onSetDisabled: () => undefined,
        onDelete: () => undefined,
        disablingAccountId: null
      }
    })
  );
};

const user = { sessionType: 'user', accountId: 'me' };
const admin = { sessionType: 'admin', accountId: 'owner' };

test("a user-role account sees no actions and no preference rows on another account's session", () => {
  const { controls, components } = drawSessionDetail(
    sessionOf('other-account'),
    otherAccount,
    user
  );
  assert.deepEqual(
    controls,
    [],
    `another account's live session offers a user-role account these controls: ${controls.join(', ')}`
  );
  assert.equal(
    components.includes('SessionPreferences'),
    false,
    "another account's session draws its preference rows for a user-role account"
  );
  assert.equal(
    components.includes('ErrorBlock'),
    false,
    "another account's session draws the guest defaults error and Retry for a user-role account"
  );

  const ended = drawSessionDetail(
    sessionOf('other-account', { isRevoked: true, revokedAt: '2026-11-14T09:30:00Z' }),
    otherAccount,
    user
  );
  assert.deepEqual(
    ended.controls,
    [],
    `another account's ended session offers a user-role account: ${ended.controls.join(', ')}`
  );
});

test('a user-role account keeps its own session rows, and an admin keeps everyone else', () => {
  const own = drawSessionDetail(
    sessionOf('me', { id: 'me-other-device' }),
    { ...otherAccount, id: 'me', username: 'me' },
    user
  );
  assert.ok(
    own.components.includes('SessionPreferences'),
    'its own session lost its preference rows'
  );
  assert.ok(
    own.controls.includes('activeSessions.actions.revoke'),
    `its own other device lost Revoke (controls: ${own.controls.join(', ')})`
  );
  assert.ok(
    own.controls.includes('user.panes.menu.editAccount'),
    `its own account lost Edit account (controls: ${own.controls.join(', ')})`
  );

  const byAdmin = drawSessionDetail(sessionOf('other-account'), otherAccount, admin);
  assert.ok(byAdmin.components.includes('SessionPreferences'), 'an admin lost the preference rows');
  for (const key of [
    'activeSessions.actions.revoke',
    'user.panes.menu.editAccount',
    'user.panes.menu.disableAccount',
    'user.panes.menu.deleteAccount',
    'user.panes.menu.deleteSession'
  ]) {
    assert.ok(
      byAdmin.controls.includes(key),
      `an admin lost ${key} (controls: ${byAdmin.controls.join(', ')})`
    );
  }
});

test('a user-role account gets no actions on another account, and keeps Edit on its own', () => {
  const other = renderAccountDetail(otherAccount, user);
  assert.equal(
    other.includes('<button'),
    false,
    `another account offers a user-role account a button: ${other}`
  );
  assert.equal(
    other.includes('<menu'),
    false,
    `another account offers a user-role account a menu: ${other}`
  );

  const own = renderAccountDetail({ ...otherAccount, id: 'me', username: 'me' }, user);
  assert.ok(
    own.includes('user.panes.menu.editAccount'),
    `its own account lost Edit account: ${own}`
  );

  const byAdmin = renderAccountDetail(otherAccount, admin);
  for (const key of [
    'user.accounts.actions.disable',
    'user.panes.menu.editAccount',
    'user.panes.menu.deleteAccount'
  ]) {
    assert.ok(byAdmin.includes(key), `an admin lost ${key} on another account: ${byAdmin}`);
  }
});
