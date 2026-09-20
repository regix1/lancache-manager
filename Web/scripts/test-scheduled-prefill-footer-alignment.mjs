import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const schedulesCss = readFileSync(
  new URL('../src/components/features/management/schedules/SchedulesSection.css', import.meta.url),
  'utf8'
);

test('scheduled prefill editor actions use the shared trailing footer alignment', () => {
  const actionsRule = schedulesCss.match(
    /\.scheduled-prefill-config-modal__actions\s*\{([\s\S]*?)\}/
  );

  assert.ok(actionsRule, 'Expected the Scheduled Prefill action-row rule');
  assert.match(actionsRule[1], /justify-content:\s*flex-end/);
  assert.doesNotMatch(
    schedulesCss,
    /\.scheduled-prefill-focused-dialog\s+\.scheduled-prefill-config-modal__actions\s*\{/
  );
});

test('phone editor actions retain their balanced flexible button layout', () => {
  const phoneButtons = schedulesCss.match(
    /\.scheduled-prefill-config-modal__actions\s*>\s*button\s*\{([\s\S]*?)\}/g
  );

  assert.ok(phoneButtons && phoneButtons.length >= 2);
  assert.match(phoneButtons.at(-1), /flex:\s*1 1 8\.5rem/);
});
