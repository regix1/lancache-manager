import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const calendarSource = readFileSync(
  new URL('../src/components/common/CalendarNavigation.tsx', import.meta.url),
  'utf8'
);

test('calendar month and year menus inherit the width of their triggers', () => {
  const dropdowns = [...calendarSource.matchAll(/<EnhancedDropdown[\s\S]*?\/>/g)].map(
    (match) => match[0]
  );

  assert.equal(dropdowns.length, 2);
  assert.match(dropdowns[0], /className="calendar-nav__month"/);
  assert.match(dropdowns[1], /className="calendar-nav__year"/);

  for (const dropdown of dropdowns) {
    assert.doesNotMatch(dropdown, /dropdownWidth=/);
  }
});
