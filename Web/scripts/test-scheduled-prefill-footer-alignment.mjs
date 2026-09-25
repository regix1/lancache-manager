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

test('phone editor actions share one width and give the busy line its own row', () => {
  // Footer buttons may sit inside a tooltip wrapper, so the flex share goes on each footer child.
  assert.match(
    schedulesCss,
    /\.scheduled-prefill-config-modal__actions > :not\(\.confirmation-modal__status\)\s*\{\s*flex: 1 1 7rem;\s*min-width: 0;/
  );
  assert.match(
    schedulesCss,
    /\.scheduled-prefill-config-modal__actions button\s*\{\s*width: 100%;\s*min-width: 0;/
  );
  assert.match(
    schedulesCss,
    /\.scheduled-prefill-config-modal__actions \.confirmation-modal__status\s*\{\s*flex-basis: 100%;/
  );
});
