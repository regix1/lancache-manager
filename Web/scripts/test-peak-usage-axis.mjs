import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { compileToUrl } from './transpile-module.mjs';

const {
  PEAK_USAGE_AXIS_COLUMNS,
  PEAK_USAGE_ROW_HOURS,
  isPeakUsageAxisColumn,
  peakUsageClockLabel,
  peakUsageColumn,
  peakUsageRow
} = await import(
  await compileToUrl('../src/components/features/dashboard/widgets/peakUsageAxis.ts')
);

test('the two rows are hours 0–11 and 12–23', () => {
  assert.equal(PEAK_USAGE_ROW_HOURS, 12);
  assert.equal(peakUsageRow(0), 0);
  assert.equal(peakUsageRow(11), 0);
  assert.equal(peakUsageRow(12), 1);
  assert.equal(peakUsageRow(23), 1);
});

test('15:00 shares the third column with 03:00, not the 06:00 column', () => {
  assert.equal(peakUsageColumn(3), 3);
  assert.equal(peakUsageColumn(15), 3);
  assert.equal(peakUsageRow(15), 1);
  assert.equal(peakUsageColumn(6), 6);
  assert.notEqual(peakUsageColumn(15), peakUsageColumn(6));
});

test('axis ticks are every third column of the 12-hour row', () => {
  assert.deepEqual([...PEAK_USAGE_AXIS_COLUMNS], [0, 3, 6, 9]);
  for (let column = 0; column < PEAK_USAGE_ROW_HOURS; column += 1) {
    assert.equal(isPeakUsageAxisColumn(column), column % 3 === 0);
  }
});

test('12-hour ticks are a clock face; 24-hour ticks are the real hour', () => {
  assert.equal(peakUsageClockLabel(0, false), '12');
  assert.equal(peakUsageClockLabel(3, false), '3');
  assert.equal(peakUsageClockLabel(6, false), '6');
  assert.equal(peakUsageClockLabel(9, false), '9');
  assert.equal(peakUsageClockLabel(0, true, 0), '00');
  assert.equal(peakUsageClockLabel(3, true, 0), '03');
  assert.equal(peakUsageClockLabel(6, true, 0), '06');
  assert.equal(peakUsageClockLabel(9, true, 0), '09');
  assert.equal(peakUsageClockLabel(0, true, PEAK_USAGE_ROW_HOURS), '12');
  assert.equal(peakUsageClockLabel(3, true, PEAK_USAGE_ROW_HOURS), '15');
  assert.equal(peakUsageClockLabel(6, true, PEAK_USAGE_ROW_HOURS), '18');
  assert.equal(peakUsageClockLabel(9, true, PEAK_USAGE_ROW_HOURS), '21');
});

test('12-hour layout is AM/PM plus one axis; 24-hour layout is two hour axes', async () => {
  const css = await readFile(
    new URL('../src/styles/features/dashboard.css', import.meta.url),
    'utf8'
  );
  assert.match(
    css,
    /grid-template-areas:\s*'am cells-am'\s*'pm cells-pm'\s*'\. axis'/
  );
  assert.match(
    css,
    /\.peak-usage-heatmap-block--24hour \{[\s\S]*?grid-template-areas:\s*'axis-first'\s*'cells-am'\s*'cells-pm'\s*'axis-second'/
  );
  assert.match(css, /\.peak-usage-heatmap \{[\s\S]*?grid-template-columns:\s*repeat\(12/);
  assert.match(css, /\.peak-usage-hour-axis \{[\s\S]*?grid-template-columns:\s*repeat\(12/);
});
