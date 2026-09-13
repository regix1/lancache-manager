import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

/**
 * Active and history session rows toggle on click or Enter/Space and also hold nested Buttons and
 * row-action menus. A nested-control guard has to stop the row's own Enter/Space
 * handling from firing when a Button inside it has focus, or the row swallows the keypress
 * before the Button's own activation runs. `rowToggleHandlers` is the one place that guard is
 * implemented, so the row must get its role/tabIndex/onClick/onKeyDown from there rather than
 * from a hand-rolled set of JSX attributes that has no way to share the guard.
 */

const readWebSource = (relativePath) =>
  readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const activeSessionsSource = readWebSource('src/components/features/user/ActiveSessions.tsx');
const activeSessionsFile = ts.createSourceFile(
  'ActiveSessions.tsx',
  activeSessionsSource,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

const collect = (sourceFile, matches) => {
  const found = [];
  const visit = (node) => {
    if (matches(node)) {
      found.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);
  return found;
};

/** The row's opening JSX tag: the one whose className attribute names `session-row`. */
const sessionRowOpeningElements = () => {
  const openingElements = collect(
    activeSessionsFile,
    (node) => ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)
  );
  const matches = openingElements.filter((node) =>
    node.attributes.properties.some(
      (attr) =>
        ts.isJsxAttribute(attr) &&
        attr.name.getText(activeSessionsFile) === 'className' &&
        attr.initializer &&
        ts.isStringLiteral(attr.initializer) &&
        attr.initializer.text.split(/\s+/).includes('session-row')
    )
  );
  assert.equal(matches.length, 2, 'active and history rows should both carry session-row');
  return matches;
};

test('rowToggleHandlers is imported from the shared utility', () => {
  const imports = collect(
    activeSessionsFile,
    (node) =>
      ts.isImportDeclaration(node) &&
      node.moduleSpecifier.getText(activeSessionsFile).includes('@utils/rowToggle')
  );
  assert.equal(imports.length, 1, 'ActiveSessions.tsx should import from @utils/rowToggle once');
  assert.match(imports[0].getText(activeSessionsFile), /\browToggleHandlers\b/);
});

test('each session row gets its toggle handlers from rowToggleHandlers, not hand-rolled attributes', () => {
  for (const row of sessionRowOpeningElements()) {
    const attrs = row.attributes.properties;

    const spreadCallsRowToggleHandlers = attrs.some(
      (attr) =>
        ts.isJsxSpreadAttribute(attr) &&
        ts.isCallExpression(attr.expression) &&
        attr.expression.expression.getText(activeSessionsFile) === 'rowToggleHandlers'
    );
    assert.ok(
      spreadCallsRowToggleHandlers,
      'the session row should spread {...rowToggleHandlers(...)} so onClick/onKeyDown share the nested-control guard'
    );

    const namedAttrNames = attrs
      .filter((attr) => ts.isJsxAttribute(attr))
      .map((attr) => attr.name.getText(activeSessionsFile));
    assert.ok(
      !namedAttrNames.includes('onKeyDown'),
      'the row must not hand-roll its own onKeyDown alongside the spread - that is the shape that let ' +
        "Enter/Space toggle the row and cancel a focused Button's own activation"
    );
    assert.ok(
      !namedAttrNames.includes('onClick'),
      'the row must not hand-roll its own onClick alongside the spread'
    );
  }
});
