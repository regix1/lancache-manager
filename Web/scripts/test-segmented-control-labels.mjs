import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

/**
 * A segmented control with responsive labels hides its text below the lg breakpoint, which is every
 * phone. The button under it is still a radio, and a radio without a name is announced as nothing.
 * The name is decided by three lines inside the component, lifted here and run as they ship.
 */

const source = readFileSync(
  new URL('../src/components/ui/SegmentedControl.tsx', import.meta.url),
  'utf8'
);

const line = (name) => {
  const match = source.match(new RegExp(`const ${name} =([\\s\\S]*?);\\r?\\n`));
  assert.ok(match, `${name} is not declared`);
  return match[1].trim();
};

const segmentName = new Function(
  'option',
  'showLabels',
  `const rendersLabel = ${line('rendersLabel')};
   const labelHidden = ${line('labelHidden')};
   return { rendersLabel, name: ${line('segmentName')} };`
);

test('a responsive segment keeps its name while its label is hidden', () => {
  const option = { value: 'sessions', label: 'Sessions', icon: {} };
  assert.deepEqual(segmentName(option, 'responsive'), { rendersLabel: true, name: 'Sessions' });
  assert.deepEqual(segmentName({ ...option, icon: undefined }, 'responsive'), {
    rendersLabel: true,
    name: 'Sessions'
  });
});

test('a segment whose label is always visible is not named twice', () => {
  const option = { value: 'sessions', label: 'Sessions', icon: {} };
  assert.deepEqual(segmentName(option, true), { rendersLabel: true, name: undefined });
  assert.deepEqual(segmentName({ ...option, icon: undefined }, false), {
    rendersLabel: true,
    name: undefined
  });
});

test('an icon-only segment is still named from its label or tooltip', () => {
  const option = { value: 'sessions', label: 'Sessions', icon: {} };
  assert.deepEqual(segmentName(option, false), { rendersLabel: false, name: 'Sessions' });
  assert.deepEqual(segmentName({ ...option, tooltip: 'Active sessions' }, false), {
    rendersLabel: false,
    name: 'Active sessions'
  });
  assert.deepEqual(segmentName({ ...option, label: { node: true } }, false), {
    rendersLabel: false,
    name: undefined
  });
});

test('the responsive label still hides below the breakpoint, so the appearance is unchanged', () => {
  assert.ok(source.includes("showLabels === 'responsive' ? ' hidden lg:inline-flex' : ''"));
  assert.ok(source.includes('aria-label={segmentName}'));
});
