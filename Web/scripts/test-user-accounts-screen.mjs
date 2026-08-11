import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * The account screen is meant to be assembled entirely out of components that already exist, and to
 * show the installation's own account with its row actions disabled rather than missing. Both are
 * claims about the source rather than about runtime behaviour, and there is no component renderer in
 * this repo, so they are checked by reading the file's syntax tree.
 *
 * The main-admin check resolves the local name bound to `account.isMainAdmin` and then requires
 * every reference to it to sit inside a `disabled=` attribute. That is what separates the two
 * possible readings: a screen that hides the actions would reference the same flag from a JSX
 * conditional instead, and this would go red. [67][68][69]
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const parse = (fileName, relativePath) =>
  ts.createSourceFile(
    fileName,
    readWebSource(relativePath),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX
  );

const accountsFile = parse('UserAccounts.tsx', 'src/components/features/user/UserAccounts.tsx');
const userTabFile = parse('UserTab.tsx', 'src/components/features/user/UserTab.tsx');

const collect = (node, predicate, found = []) => {
  if (predicate(node)) {
    found.push(node);
  }
  // forEachChild stops as soon as its callback returns anything truthy, so the callback must
  // return nothing or the walk ends at the first child.
  node.forEachChild((child) => {
    collect(child, predicate, found);
  });
  return found;
};

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

const jsxAttribute = (node, attributeName) => {
  const opening = ts.isJsxElement(node) ? node.openingElement : node;
  return opening.attributes.properties.find(
    (property) =>
      ts.isJsxAttribute(property) &&
      ts.isIdentifier(property.name) &&
      property.name.text === attributeName
  );
};

/** The `render` arrow function of the column whose `key` is the given literal. */
const columnRender = (sourceFile, key) => {
  const literal = collect(
    sourceFile,
    (node) =>
      ts.isObjectLiteralExpression(node) &&
      node.properties.some(
        (property) =>
          ts.isPropertyAssignment(property) &&
          property.name.getText() === 'key' &&
          ts.isStringLiteral(property.initializer) &&
          property.initializer.text === key
      )
  )[0];
  assert.ok(literal, `no column with key '${key}'`);

  const render = literal.properties.find(
    (property) => ts.isPropertyAssignment(property) && property.name.getText() === 'render'
  );
  assert.ok(render, `the '${key}' column has no render`);
  return render.initializer;
};

test('the account screen is built from the shared list components, not new ones [67]', () => {
  const imports = importedNamesByModule(accountsFile);

  const required = {
    '@components/ui/DataTable': ['DataTable', 'DataTableColumn'],
    '@components/ui/Pagination': ['Pagination'],
    '@components/ui/ActionMenu': ['ActionMenu', 'ActionMenuItem', 'ActionMenuDangerItem'],
    '@components/ui/ManagerCard': ['EmptyState', 'LoadingState'],
    '@components/common/ConfirmationModal': ['ConfirmationModal'],
    '@components/ui/Badge': ['Badge']
  };

  for (const [module, names] of Object.entries(required)) {
    const imported = imports.get(module);
    assert.ok(imported, `UserAccounts.tsx does not import from ${module}`);
    for (const name of names) {
      assert.ok(imported.includes(name), `${name} is not imported from ${module}`);
    }
  }

  // A second table, pager, empty state or row menu declared here would defeat the point of the
  // imports above, so nothing in the file may declare one.
  const declaredNames = collect(
    accountsFile,
    (node) => ts.isVariableDeclaration(node) && ts.isIdentifier(node.name)
  ).map((node) => node.name.text);

  for (const name of declaredNames) {
    assert.ok(
      !/^(DataTable|Pagination|ActionMenu|EmptyState|LoadingState|ConfirmationModal|Badge)/.test(
        name
      ),
      `UserAccounts.tsx declares its own ${name}`
    );
  }
});

test('the screen defines an empty, a loading and an error state [68]', () => {
  const rendered = new Set(
    collect(accountsFile, (node) => ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node)).map(
      jsxTagName
    )
  );

  for (const tag of ['LoadingState', 'EmptyState', 'Alert']) {
    assert.ok(rendered.has(tag), `the screen never renders ${tag}`);
  }

  const calls = collect(accountsFile, (node) => ts.isCallExpression(node)).map((node) =>
    node.expression.getText()
  );
  assert.ok(calls.includes('useErrorHandler'), 'the screen does not use useErrorHandler');
  assert.ok(calls.includes('notifyError'), 'the screen never calls notifyError');
});

test("the installation's own account keeps its row actions, disabled [69]", () => {
  const render = columnRender(accountsFile, 'actions');

  const ownerDeclaration = collect(
    render,
    (node) =>
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.initializer !== undefined &&
      node.initializer.getText() === 'account.isMainAdmin'
  )[0];
  assert.ok(ownerDeclaration, 'the actions column never reads account.isMainAdmin');
  const ownerName = ownerDeclaration.name.text;

  const items = collect(
    render,
    (node) =>
      (ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node)) &&
      ['ActionMenuItem', 'ActionMenuDangerItem'].includes(jsxTagName(node))
  );
  assert.ok(items.length >= 4, `expected the row menu to keep its actions, found ${items.length}`);

  for (const item of items) {
    const disabled = jsxAttribute(item, 'disabled');
    assert.ok(disabled, `${jsxTagName(item)} has no disabled prop`);
    assert.ok(
      disabled.initializer.getText().includes(ownerName),
      `${jsxTagName(item)} is not disabled for the main administrator`
    );
  }

  // Every other reference to the flag would be a way of removing an action instead of disabling it.
  const references = collect(
    render,
    (node) => ts.isIdentifier(node) && node.text === ownerName && node !== ownerDeclaration.name
  );

  for (const reference of references) {
    let ancestor = reference.parent;
    let insideDisabled = false;
    while (ancestor && ancestor !== render) {
      if (ts.isJsxAttribute(ancestor) && ancestor.name.getText() === 'disabled') {
        insideDisabled = true;
        break;
      }
      ancestor = ancestor.parent;
    }
    assert.ok(
      insideDisabled,
      `${ownerName} is read outside a disabled prop, which hides an action instead of disabling it`
    );
  }
});

test('the account screen is a third segment of the user tab [67]', () => {
  const options = collect(
    userTabFile,
    (node) =>
      ts.isObjectLiteralExpression(node) &&
      node.properties.some(
        (property) =>
          ts.isPropertyAssignment(property) &&
          property.name.getText() === 'value' &&
          ts.isStringLiteral(property.initializer) &&
          property.initializer.text === 'accounts'
      )
  );
  assert.equal(options.length, 1, 'the segmented control has no accounts segment');

  const rendered = collect(
    userTabFile,
    (node) => ts.isJsxElement(node) || ts.isJsxSelfClosingElement(node)
  ).map(jsxTagName);
  assert.ok(rendered.includes('UserAccounts'), 'the accounts segment renders nothing');
});
