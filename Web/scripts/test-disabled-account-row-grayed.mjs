import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import React from 'react';
import typescript from 'typescript';
import { featureTree, inert } from './component-tree.mjs';
import { bindLifted, liftConstArrow } from './transpile-module.mjs';

/**
 * A disabled account's row in the Users page list reads grayed out: the row the list draws for it
 * carries a class the enabled row does not, and the app's stylesheets give that class a muted or
 * disabled theme color, never opacity on the whole row (which would also fade the focus ring). The
 * row stays a button the admin can pick, so the account can be opened and enabled again. The list's
 * shipped account row is drawn for an enabled and a disabled account.
 */

const TAB = 'src/components/features/user/UserTab.tsx';
const ROW = 'src/components/features/user/UserListRow.tsx';

const { Root: UserListRow, draw } = await featureTree(ROW, 'UserListRow', { React });
const StatusDot = inert('StatusDot');

/** The list's account row for `account`, drawn down to plain elements. */
const drawnRow = (account) => {
  const picks = [];
  const accountRow = bindLifted(
    liftConstArrow(TAB, 'accountRow'),
    {
      React,
      UserListRow,
      StatusDot,
      activeSessionsOf: () => [],
      getSessionStatus: () => 'idle',
      t: (key) => key,
      accountId: 'the-admin',
      accountLine: () => 'Member',
      shownPick: null,
      choose: (pick) => picks.push(pick)
    },
    { jsx: typescript.JsxEmit.React }
  );
  return { tree: draw(accountRow(account)), picks };
};

/** Every element in a drawn tree. */
const elementsOf = (node) => {
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) value.forEach(walk);
    else if (React.isValidElement(value)) {
      found.push(value);
      Object.values(value.props).forEach(walk);
    }
  };
  walk(node);
  return found;
};

const classesOf = (tree) =>
  new Set(
    elementsOf(tree).flatMap((element) =>
      typeof element.props.className === 'string' ? element.props.className.split(/\s+/) : []
    )
  );

/** Every rule in the app's stylesheets, as selector and body. */
const cssRules = () => {
  const files = [];
  const walk = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = new URL(entry.name + (entry.isDirectory() ? '/' : ''), directory);
      if (entry.isDirectory()) walk(path);
      else if (entry.name.endsWith('.css')) files.push(path);
    }
  };
  walk(new URL('../src/', import.meta.url));
  return files.flatMap((file) =>
    [...readFileSync(file, 'utf8').matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((match) => ({
      selector: match[1].replace(/\/\*[\s\S]*?\*\//g, '').trim(),
      body: match[2]
    }))
  );
};

const account = (isDisabled) => ({
  id: isDisabled ? 'disabled-member' : 'enabled-member',
  username: isDisabled ? 'disabled-member' : 'enabled-member',
  role: 'user',
  isMainAdmin: false,
  isDisabled,
  lastLoginAtUtc: null
});

test('a disabled account reads grayed out in the Users list and can still be picked', () => {
  const enabled = drawnRow(account(false));
  const disabled = drawnRow(account(true));
  const enabledClasses = classesOf(enabled.tree);
  const added = [...classesOf(disabled.tree)].filter((name) => !enabledClasses.has(name));
  const rules = cssRules();
  const wrong = [];

  const grays = added.filter((name) =>
    rules.some(
      (rule) =>
        new RegExp(`\\.${name}(?![\\w-])`).test(rule.selector) &&
        /(^|;|\s)color\s*:\s*var\(--[\w-]*(muted|disabled)[\w-]*\)/.test(rule.body)
    )
  );
  if (grays.length === 0)
    wrong.push(
      added.length === 0
        ? 'the disabled account is drawn with the same classes as an enabled one'
        : `no stylesheet gives ${added.join(', ')} a muted or disabled theme color`
    );
  for (const name of added) {
    if (
      rules.some(
        (rule) =>
          new RegExp(`\\.${name}$`).test(rule.selector.split(',').at(-1).trim()) &&
          /(^|;|\s)opacity\s*:/.test(rule.body)
      )
    )
      wrong.push(`the stylesheets fade the whole disabled row with opacity (.${name})`);
  }
  if (elementsOf(disabled.tree).some((element) => element.props.style !== undefined))
    wrong.push('the disabled row carries an inline style');

  const button = elementsOf(disabled.tree).find((element) => element.type === 'button');
  if (!button || button.props.disabled === true)
    wrong.push('the disabled account cannot be picked');
  else {
    button.props.onClick();
    if (disabled.picks.length !== 1) wrong.push('picking the disabled account opens nothing');
  }
  assert.deepEqual(wrong, [], `the disabled account's row: ${wrong.join('; ')}`);
});
