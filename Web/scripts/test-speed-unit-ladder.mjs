import assert from 'node:assert/strict';
import test from 'node:test';
import { compileToUrl, moduleUrl } from './transpile-module.mjs';

/**
 * The two speed formatters have to read one unit ladder.
 *
 * They held verbatim copies of it - same bit conversion, same 1024 step, same six units - so a unit
 * added to one of them did not exist in the other, and the split display would have gone on saying
 * "Pb/s" while the single-string one had already moved past it. They now share `speedInBits`, and
 * these cases are what stops a later edit forking them again: every one asserts that the string
 * formatter says exactly what the split formatter's two halves say together.
 *
 * Zero is deliberately NOT shared. The two disagree about what an idle readout reads, and that
 * disagreement is in the callers, not the ladder.
 */

const { formatSpeed, formatSpeedWithSeparatedUnit } = await import(
  await compileToUrl('../src/utils/formatters.ts', {
    '@/i18n': moduleUrl(`export default { t: (key) => key };`),
    './constants': moduleUrl(`export const FILE_SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];`),
    './dateTimeFormat': moduleUrl(`export const formatTimestamp = () => '';`)
  })
);

/** One rate per rung of the ladder, plus a rate that lands mid-rung. */
const RATES = [
  1, // b/s
  128, // 1 Kb/s
  1024 * 128, // 1 Mb/s
  1024 * 1024 * 128, // 1 Gb/s
  1024 * 1024 * 1024 * 128, // 1 Tb/s
  1024 * 1024 * 1024 * 1024 * 128, // 1 Pb/s
  1024 * 1024 * 1024 * 1024 * 1024 * 128, // past the last rung
  1_500_000, // mid-rung, exercises the rounding
  333
];

for (const rate of RATES) {
  test(`both formatters read the same rung at ${rate} bytes/s`, () => {
    const parts = formatSpeedWithSeparatedUnit(rate);
    assert.equal(formatSpeed(rate), `${parts.value} ${parts.unit}`);
  });
}

test('the decimal count reaches the shared ladder', () => {
  assert.equal(formatSpeed(1_500_000, 3), '11.444 Mb/s');
  assert.deepEqual(formatSpeedWithSeparatedUnit(1_500_000, 3), {
    value: '11.444',
    unit: 'Mb/s'
  });
});

test('a rate past the last rung falls back to bits rather than going undefined', () => {
  assert.match(formatSpeed(RATES[6]), /b\/s$/);
  assert.equal(formatSpeedWithSeparatedUnit(RATES[6]).unit, 'b/s');
});

test('the two keep their own answers for an idle rate', () => {
  assert.equal(formatSpeed(0), 'common.notAvailable');
  assert.deepEqual(formatSpeedWithSeparatedUnit(0), { value: '0', unit: 'b/s' });
});
