import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import { collectNodes, declaredValue, make, parseSource } from './transpile-module.mjs';

/**
 * Accounts are one filter of the Users list, and a picked account opens its detail pane. The pane
 * is assembled out of components that already exist. It leaves out the actions the server refuses:
 * the primary admin gets no Disable button and no menu, and your own account keeps Edit but loses
 * Disable and Delete.
 */

const USER_FOLDER = 'src/components/features/user/';

const detailFile = parseSource(`${USER_FOLDER}AccountDetail.tsx`);
const dialogsFile = parseSource(`${USER_FOLDER}AccountDialogs.tsx`);
const userTabFile = parseSource(`${USER_FOLDER}UserTab.tsx`);
const accountsHookFile = parseSource(`${USER_FOLDER}useUserAccounts.ts`);

const importedNamesByModule = (sourceFile) => {
  const byModule = new Map();
  for (const statement of sourceFile.statements) {
    if (!ts.isImportDeclaration(statement) || !ts.isStringLiteral(statement.moduleSpecifier)) {
      continue;
    }
    const names = [];
    const clause = statement.importClause;
    if (clause?.name) {
      names.push(clause.name.text);
    }
    if (clause?.namedBindings && ts.isNamedImports(clause.namedBindings)) {
      for (const element of clause.namedBindings.elements) {
        names.push(element.name.text);
      }
    }
    byModule.set(statement.moduleSpecifier.text, names);
  }
  return byModule;
};

const jsxTagName = (node) => {
  const opening = ts.isJsxElement(node) ? node.openingElement : node;
  return opening.tagName.getText();
};

const renderedTags = (sourceFile) =>
  new Set(
    collectNodes(
      sourceFile,
      (node) => ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node)
    ).map(jsxTagName)
  );

/** The shipped account pane, rendered for the viewer signed in as `viewerId`. */
const renderAccountDetail = (account, viewerId) => {
  const UserPaneHeader = make(`${USER_FOLDER}UserPaneHeader.tsx`, 'UserPaneHeader', { React });
  const AccountDetail = make(`${USER_FOLDER}AccountDetail.tsx`, 'AccountDetail', {
    React,
    useState: React.useState,
    useMemo: React.useMemo,
    useTranslation: () => ({ t: (key) => key }),
    useAuth: () => ({ accountId: viewerId }),
    useReaderClock: () => ({}),
    formatTimestamp: () => 'created',
    formatLastSeen: () => 'last seen',
    UserPaneHeader,
    Button: ({ children }) => React.createElement('button', { 'data-action': 'button' }, children),
    // The menu's items render in place, so the markup lists every item the menu would hold.
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
      account: {
        id: 'account-a',
        username: 'alice',
        isMainAdmin: false,
        isDisabled: false,
        createdAtUtc: '2026-01-01T00:00:00Z',
        lastLoginAtUtc: null,
        ...account
      },
      accountActions: {
        onEdit: () => undefined,
        onSetDisabled: () => undefined,
        onDelete: () => undefined,
        disablingAccountId: null
      }
    })
  );
};

test('the account pane is built from the shared components, not new ones', () => {
  const required = [
    [detailFile, '@components/ui/RowActionsMenu', ['RowActionsMenu']],
    [detailFile, '@components/ui/ActionMenu', ['ActionMenuItem']],
    [detailFile, '@components/ui/Button', ['Button']],
    [dialogsFile, '@components/common/ConfirmationModal', ['ConfirmationModal']],
    [dialogsFile, '@components/ui/Modal', ['Modal']],
    [userTabFile, '@components/ui/ManagerCard', ['EmptyState', 'LoadingState']],
    [userTabFile, '@components/ui/Badge', ['Badge']]
  ];

  for (const [sourceFile, module, names] of required) {
    const imported = importedNamesByModule(sourceFile).get(module);
    assert.ok(imported, `${sourceFile.fileName} does not import from ${module}`);
    for (const name of names) {
      assert.ok(imported.includes(name), `${name} is not imported from ${module}`);
    }
  }

  // A second menu, dialog or empty state declared here would defeat the point of the imports
  // above, so nothing in these files may declare one.
  for (const sourceFile of [detailFile, dialogsFile]) {
    const declaredNames = collectNodes(
      sourceFile,
      (node) => ts.isVariableDeclaration(node) && ts.isIdentifier(node.name)
    ).map((node) => node.name.text);
    for (const name of declaredNames) {
      assert.ok(
        !/^(ActionMenu|RowActionsMenu|EmptyState|LoadingState|ConfirmationModal|Modal|Badge)/.test(
          name
        ),
        `${sourceFile.fileName} declares its own ${name}`
      );
    }
  }
});

test('the accounts list defines an empty, a loading and an error state', () => {
  const rendered = renderedTags(userTabFile);
  for (const tag of ['LoadingState', 'EmptyState', 'ErrorBlock']) {
    assert.ok(rendered.has(tag), `the Users list never renders ${tag}`);
  }

  const calls = collectNodes(accountsHookFile, (node) => ts.isCallExpression(node)).map((node) =>
    node.expression.getText()
  );
  assert.ok(calls.includes('useErrorHandler'), 'the accounts hook does not use useErrorHandler');
  assert.ok(calls.includes('notifyError'), 'the accounts hook never calls notifyError');
});

test('the primary admin account shows no account actions, and says why', () => {
  const markup = renderAccountDetail({ isMainAdmin: true }, 'someone-else');
  assert.doesNotMatch(markup, /<button/, 'the primary admin has a Disable button');
  assert.doesNotMatch(markup, /<menu/, 'the primary admin has an actions menu');
  assert.match(markup, /user\.panes\.help\.primaryAdmin/);
});

test('your own account keeps Edit and cannot be disabled or deleted', () => {
  const markup = renderAccountDetail({}, 'account-a');
  assert.doesNotMatch(markup, /<button/, 'your own account has a Disable button');
  assert.match(markup, /<li>user\.panes\.menu\.editAccount<\/li>/);
  assert.doesNotMatch(markup, /user\.panes\.menu\.deleteAccount/);
});

test("another user's account offers Disable, Edit and Delete", () => {
  const markup = renderAccountDetail({}, 'someone-else');
  assert.match(markup, /<button[^>]*>user\.accounts\.actions\.disable<\/button>/);
  assert.match(markup, /<li>user\.panes\.menu\.editAccount<\/li>/);
  assert.match(markup, /<li>user\.panes\.menu\.deleteAccount<\/li>/);
});

test('account creation and account actions contain no assignable role surface', () => {
  for (const sourceFile of [detailFile, dialogsFile, accountsHookFile, userTabFile]) {
    const source = sourceFile.getText();
    for (const removed of [
      '/role',
      'editor.role',
      'open.role',
      'actions.promote',
      'actions.demote',
      'confirm.role',
      'errors.role',
      'adminRoleReserved',
      'user.accounts.roles.admin'
    ]) {
      assert.equal(source.includes(removed), false, `${sourceFile.fileName} contains ${removed}`);
    }
  }

  assert.match(
    accountsHookFile.getText(),
    /\{ username: open\.username, password: open\.password \}/,
    'new accounts must post only username and password'
  );
  assert.match(
    detailFile.getText(),
    /primaryAdmin \? t\('user\.accounts\.mainAdmin'\) : t\('user\.accounts\.roles\.user'\)/
  );
});

test('accounts are a filter of the Users list and open the account pane', () => {
  assert.ok(declaredValue(`${USER_FOLDER}UserTab.tsx`, 'LIST_FILTERS').includes('accounts'));
  const rendered = renderedTags(userTabFile);
  assert.ok(rendered.has('AccountDetail'), 'a picked account opens nothing');
  assert.ok(rendered.has('AccountDialogs'), 'the account dialogs are not mounted');
});
