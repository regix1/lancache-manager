import assert from 'node:assert/strict';
import test from 'node:test';
import React from 'react';
import { busyChange, LoadingSpinner } from './markup-shape.mjs';
import { make, translator } from './transpile-module.mjs';

/**
 * Each service row in Log Removal has a Clear button. While that row's removal runs, the button keeps
 * the box it has at rest: the busy sign sits over the label, and the label is not swapped for a longer
 * one, which would widen the button and, under the busy overlay, show a lone spinner in a wider box.
 * The shipped row is drawn with the shipped Button, at rest and while removing, with the labels the
 * Log Removal list hands it, and the two markups are compared. The pixel width needs a browser and is
 * measured separately.
 */

const translate = translator('en');
const passthrough = ({ children }) => React.createElement(React.Fragment, null, children);
const Button = make('src/components/ui/Button.tsx', 'Button', {
  React,
  LoadingSpinner,
  useOptionalDirectoryPermissionsContext: () => null
});
const ServiceRow = make(
  'src/components/features/management/log-processing/LogRemovalManager.tsx',
  'ServiceRow',
  {
    React,
    Button,
    Checkbox: () => null,
    DisabledActionGate: passthrough,
    getServiceDisplayName: (service) => service,
    formatCount: (count) => String(count)
  }
);

test('a Log Removal Clear button keeps the shape it has at rest while its removal runs', () => {
  const service = 'steam';
  const draw = (isRemoving) =>
    React.createElement(ServiceRow, {
      service,
      count: 1200,
      isRemoving,
      isDisabled: false,
      onClick: () => undefined,
      clearLabel: translate('management.logRemoval.buttons.clear'),
      entriesLabel: translate('management.logRemoval.labels.entries'),
      selectable: false,
      selected: false,
      onSelectToggle: () => undefined,
      selectLabel: '',
      selectDisabled: false,
      selectDisabledReason: '',
      clearTooltip: ''
    });
  const { restOutline, busyOutline } = busyChange(draw(false), draw(true));
  assert.equal(
    busyOutline,
    restOutline,
    `the Clear button changes what it holds while its removal runs: at rest ${restOutline}, removing ${busyOutline}`
  );
});
