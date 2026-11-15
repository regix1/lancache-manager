import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ts from 'typescript';
import { compileToUrl, make, moduleUrl } from './transpile-module.mjs';

/**
 * A Users list row is one native button and holds no control of its own, so a click or a key on
 * it can only pick the row. Rows that do hold nested Buttons and menus (prefill sessions, events,
 * cache lists) toggle through `rowToggleHandlers`, whose guard stops the row's own Enter/Space
 * handling from firing when a control inside it has focus.
 */

const UserListRow = make('src/components/features/user/UserListRow.tsx', 'UserListRow', {
  React,
  ChevronRight: () => null
});

const renderRow = (props) =>
  renderToStaticMarkup(
    React.createElement(UserListRow, {
      lead: React.createElement('span', null, 'lead'),
      title: 'alice',
      selected: false,
      onSelect: () => undefined,
      ...props
    })
  );

test('a Users list row is one button with no button inside it', () => {
  for (const props of [
    { line: 'Firefox on Windows', aside: 'you', selected: true },
    { line: null, quiet: true }
  ]) {
    const markup = renderRow(props);
    assert.equal(markup.match(/<button\b/g)?.length, 1, markup);
    assert.match(
      markup,
      /^<li><button type="button"[^>]*>.*<\/button><\/li>$/s,
      'the button is the row'
    );
  }
});

test('a Users list row with no line renders no line element', () => {
  assert.doesNotMatch(renderRow({ line: null }), /users-row__line/);
  assert.match(renderRow({ line: 'Firefox on Windows' }), /users-row__line/);
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
