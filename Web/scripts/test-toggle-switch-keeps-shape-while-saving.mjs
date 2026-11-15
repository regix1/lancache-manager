import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { busyChange, LoadingSpinner } from './markup-shape.mjs';
import { compileTree, make, translator } from './transpile-module.mjs';

/**
 * An On/Off switch saves when it is clicked, and while it saves it must keep its size, place and
 * brightness: the busy sign sits on content the switch already draws or over it, never beside the
 * label where it widens the pill, and the switch is not dimmed: a control that jumps in size or
 * fades for a moment on every click reads as broken. The shipped switch is drawn
 * here with the shipped On/Off options (`Web/src/components/features/user/onOffOptions.ts`) at rest
 * and while it saves, at both sizes and on both values, and the two markups are compared. The pixel
 * width at 1440 and 390 needs a browser and is measured separately.
 */

const translate = translator('en');
const { getOnOffOptions } = await import(
  await compileTree('../src/components/features/user/onOffOptions.ts')
);
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);
const ToggleSwitch = make('src/components/ui/ToggleSwitch.tsx', 'ToggleSwitch', {
  React,
  LoadingSpinner,
  Tooltip: passthrough
});

test('an On/Off switch that is saving keeps the shape and brightness it has at rest', () => {
  const options = getOnOffOptions(translate);
  const wrong = [];
  for (const size of ['sm', 'md']) {
    for (const value of ['off', 'on']) {
      const draw = (loading) =>
        React.createElement(ToggleSwitch, {
          options,
          value,
          onChange: () => undefined,
          loading,
          size
        });
      const { restOutline, busyOutline, addedDimming } = busyChange(draw(false), draw(true));
      if (busyOutline !== restOutline)
        wrong.push(`${size} switch on ${value}: at rest ${restOutline}, saving ${busyOutline}`);
      if (addedDimming.length > 0)
        wrong.push(`${size} switch on ${value}: saving adds ${addedDimming.join(' ')}`);
    }
  }

  assert.deepEqual(
    wrong,
    [],
    `a saving On/Off switch adds content beside its label or dims: ${wrong.join('; ')}`
  );
});
