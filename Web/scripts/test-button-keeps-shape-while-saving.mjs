import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { busyChange, LoadingSpinner } from './markup-shape.mjs';
import { make } from './transpile-module.mjs';

/**
 * A button that saves keeps its box while it saves: the busy sign sits over the label or in room
 * the button already holds, so the button does not grow and the row beside it does not move. Most saving
 * buttons in the app pass `loading` with no left icon and no other prop, so the shipped button is
 * drawn that way at rest and while it saves, at every size and with and without a right icon, and
 * the two markups are compared. The pixel width needs a browser and is measured separately.
 */

const Button = make('src/components/ui/Button.tsx', 'Button', {
  React,
  LoadingSpinner,
  useOptionalDirectoryPermissionsContext: () => null
});
const chevron = React.createElement('svg', { className: 'w-4 h-4', 'aria-hidden': true });

test('a saving button with no left icon keeps the shape it has at rest', () => {
  const wrong = [];
  for (const size of ['xs', 'sm', 'md', 'lg']) {
    for (const [layout, rightSection] of [
      ['label only', undefined],
      ['label and right icon', chevron]
    ]) {
      const draw = (loading) =>
        React.createElement(
          Button,
          { size, loading, rightSection, onClick: () => undefined },
          'Save'
        );
      const { restOutline, busyOutline, addedDimming } = busyChange(draw(false), draw(true));
      if (busyOutline !== restOutline)
        wrong.push(`${size} button, ${layout}: at rest ${restOutline}, saving ${busyOutline}`);
      if (addedDimming.length > 0)
        wrong.push(`${size} button, ${layout}: saving adds ${addedDimming.join(' ')}`);
    }
  }

  assert.deepEqual(
    wrong,
    [],
    `a saving button adds content beside its label, so it changes width: ${wrong.join('; ')}`
  );
});
