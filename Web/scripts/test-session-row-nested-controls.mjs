import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

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

// Answers `closest` for the selectors the row guard uses: a tag name, or one or more attribute
// tests such as `[role="group"][tabindex]`.
class FakeElement {
  constructor(tag, attributes, parent) {
    this.tag = tag;
    this.attributes = attributes;
    this.parentElement = parent;
  }
  matches(selector) {
    const tests = [...selector.matchAll(/\[([a-z-]+)(?:="([^"]*)")?\]/g)];
    if (tests.length === 0) return this.tag === selector;
    return tests.every(([, name, value]) =>
      value === undefined ? name in this.attributes : this.attributes[name] === value
    );
  }
  closest(selectorList) {
    const selectors = selectorList.split(',').map((part) => part.trim());
    if (selectors.some((selector) => this.matches(selector))) return this;
    return this.parentElement === null ? null : this.parentElement.closest(selectorList);
  }
}

test('Enter or Space on a disabled control reason stop leaves the row as it is', async () => {
  const previous = { Element: globalThis.Element, HTMLElement: globalThis.HTMLElement };
  globalThis.Element = FakeElement;
  globalThis.HTMLElement = FakeElement;
  try {
    const { rowToggleHandlers } = await import(await compileToUrl('../src/utils/rowToggle.ts'));
    let toggles = 0;
    const handlers = rowToggleHandlers(() => toggles++);
    const row = new FakeElement('div', { role: 'button', tabindex: '0' }, null);
    const press = (key, target) =>
      handlers.onKeyDown({ key, target, currentTarget: row, preventDefault: () => undefined });
    const tooltip = new FakeElement('div', {}, row);
    // The Tab stop DisabledActionGate puts in place of a disabled control that has a reason.
    const stop = new FakeElement('span', { role: 'group', tabindex: '0' }, tooltip);
    press('Enter', stop);
    press(' ', stop);
    assert.equal(toggles, 0, 'a key on the reason stop toggled the row');
    // A group with no Tab stop is plain row content, and the row still toggles on its own keys.
    press('Enter', new FakeElement('div', { role: 'group' }, row));
    press('Enter', row);
    press(' ', row);
    assert.equal(toggles, 3, 'the row no longer toggles on its own keys');
  } finally {
    globalThis.Element = previous.Element;
    globalThis.HTMLElement = previous.HTMLElement;
  }
});

test('Space on the reason stop of a disabled control does not scroll the page or toggle the row', async () => {
  const react = import.meta.resolve('react');
  // The tooltip keeps the stop it is handed, so the test can press keys on the stop the gate renders.
  const tooltipUrl = moduleUrl(
    `export const held = { stop: null };\n` +
      'export const Tooltip = ({ children }) => { held.stop = children; return null; };'
  );
  const { held } = await import(tooltipUrl);
  const { DisabledActionGate } = await import(
    await compileToUrl(
      '../src/components/features/management/DisabledActionGate.tsx',
      { react, '@components/ui/Tooltip': tooltipUrl },
      { jsx: ts.JsxEmit.React }
    )
  );
  const renderStop = (disabled, reason) => {
    renderToStaticMarkup(
      React.createElement(
        DisabledActionGate,
        { disabled, reason, label: 'Remove' },
        React.createElement('button', { disabled }, 'Remove')
      )
    );
    return held.stop.props;
  };
  const previous = { Element: globalThis.Element, HTMLElement: globalThis.HTMLElement };
  globalThis.Element = FakeElement;
  globalThis.HTMLElement = FakeElement;
  try {
    const { rowToggleHandlers } = await import(await compileToUrl('../src/utils/rowToggle.ts'));
    let toggles = 0;
    const handlers = rowToggleHandlers(() => toggles++);
    const row = new FakeElement('div', { role: 'button', tabindex: '0' }, null);
    const stopProps = renderStop(true, 'A removal is running');
    const stop = new FakeElement(
      'span',
      { role: stopProps.role, tabindex: String(stopProps.tabIndex) },
      row
    );
    // The key reaches the stop first and then bubbles to the row.
    const press = (key) => {
      let prevented = false;
      const preventDefault = () => {
        prevented = true;
      };
      stopProps.onKeyDown?.({ key, target: stop, currentTarget: stop, preventDefault });
      handlers.onKeyDown({ key, target: stop, currentTarget: row, preventDefault });
      return prevented;
    };
    assert.equal(press(' '), true, 'Space on the reason stop scrolls the page');
    assert.equal(press('Enter'), false, 'Enter on the reason stop was swallowed');
    assert.equal(toggles, 0, 'a key on the reason stop toggled the row');
    // An enabled control has no stop of its own, so it keeps every key.
    assert.equal(renderStop(false, '').onKeyDown, undefined);
  } finally {
    globalThis.Element = previous.Element;
    globalThis.HTMLElement = previous.HTMLElement;
  }
});
