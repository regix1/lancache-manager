import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';
import { transpile } from './transpile-module.mjs';

/**
 * A guest whose administrator allows three of the five clock faces sees the other two greyed out.
 * The row says which clock it is and nothing about why it cannot be chosen, so the reader is left
 * to guess between a permission and a broken control. The row that refuses has to say so.
 *
 * This repo has no component renderer, so the row is built by the picker's own mapping, taken out
 * of the file's syntax tree and run against the values the component holds when a guest opens it.
 */

const SELECTOR_PATH = 'src/components/common/TimezoneSelector.tsx';

const sourceFile = ts.createSourceFile(
  'TimezoneSelector.tsx',
  readFileSync(new URL(`../${SELECTOR_PATH}`, import.meta.url), 'utf8'),
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.TSX
);

/** The arrow the picker maps its five options through, as a callable. */
const readOptionMapping = () => {
  const calls = [];
  const visit = (node) => {
    if (
      ts.isCallExpression(node) &&
      ts.isPropertyAccessExpression(node.expression) &&
      node.expression.expression.getText(sourceFile) === 'options' &&
      node.expression.name.text === 'map'
    ) {
      calls.push(node);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(sourceFile, visit);

  assert.equal(calls.length, 1, `expected exactly one options.map in ${SELECTOR_PATH}`);
  assert.equal(calls[0].arguments.length, 1, 'expected options.map to take one argument');

  // The compiler puts its own prologue in front of the expression, which a `return` would stop at.
  const body = transpile(`(${calls[0].arguments[0].getText(sourceFile)})`).replace(
    /^"use strict";\s*/,
    ''
  );
  return new Function('t', 'adminDefault', 'effectiveAllowedFormats', `return ${body}`);
};

/** The five faces as the picker receives them, each described by the clock it reads. */
const OPTIONS = [
  { value: 'server-24h', label: 'Server (24h)', description: 'Server timezone, 24-hour format' },
  { value: 'server-12h', label: 'Server (12h)', description: 'Server timezone, 12-hour format' },
  { value: 'local-24h', label: 'Local (24h)', description: 'Your local timezone, 24-hour format' },
  { value: 'local-12h', label: 'Local (12h)', description: 'Your local timezone, 12-hour format' },
  { value: 'utc', label: 'UTC', description: 'Universal time standard' }
];

const ALLOWED = ['server-24h', 'local-24h', 'local-12h'];

const rowsFor = (allowed) => {
  const mapping = readOptionMapping()((key) => key, null, allowed);
  return new Map(OPTIONS.map((option) => [option.value, mapping(option)]));
};

test('a clock face the administrator has not allowed says so instead of describing itself', () => {
  const rows = rowsFor(ALLOWED);

  assert.equal(rows.get('utc').disabled, true, 'the allow-list has to reach the row');
  assert.equal(
    rows.get('utc').description,
    'common.timezoneSelector.notAllowed',
    'a row the reader cannot pick has to give the reason, not the clock it would have read'
  );
  assert.equal(rows.get('server-12h').description, 'common.timezoneSelector.notAllowed');
});

test('a clock face that is allowed still describes itself', () => {
  const rows = rowsFor(ALLOWED);

  assert.equal(rows.get('local-24h').disabled, false);
  assert.equal(
    rows.get('local-24h').description,
    'Your local timezone, 24-hour format',
    'the rows a reader can pick are unchanged'
  );
});

test('an install with no allow-list describes every face', () => {
  const rows = rowsFor([]);

  for (const option of OPTIONS) {
    assert.equal(rows.get(option.value).disabled, false, `${option.value} was refused`);
    assert.equal(rows.get(option.value).description, option.description);
  }
});
