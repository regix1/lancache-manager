import assert from 'node:assert/strict';
import test from 'node:test';
import { registerHooks } from 'node:module';

// The source modules import each other without a file extension, which node's resolver rejects on
// its own. Falling back to the .ts sibling is enough to load them here with type stripping.
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier.startsWith('.') && !/\.[cm]?[jt]sx?$/.test(specifier)) {
      try {
        return nextResolve(specifier, context);
      } catch {
        return nextResolve(`${specifier}.ts`, context);
      }
    }
    return nextResolve(specifier, context);
  }
});

const { getDayBoundsInTimezone, getDayStartInTimezone } = await import('../src/utils/timezone.ts');
const { formatTimestamp } = await import('../src/utils/dateTimeFormat.ts');

const DAY_MS = 24 * 60 * 60 * 1000;
const PREVIEW_DAYS = 3700;

const collectWarnings = (run) => {
  const warnings = [];
  const original = console.warn;
  console.warn = (...args) => warnings.push(args.join(' '));
  try {
    run();
  } finally {
    console.warn = original;
  }
  return warnings;
};

// Runs first on purpose: the fallback for an unresolvable id is remembered for the life of the
// process, so a later test touching the same id would find it already recorded.
test('an unresolvable zone warns once across a full preview instead of once per day', () => {
  const warnings = collectWarnings(() => {
    for (let day = 0; day < PREVIEW_DAYS; day++) {
      getDayBoundsInTimezone(new Date(Date.UTC(2026, 0, 1) + day * DAY_MS), 'Nowhere/Imaginary');
    }
  });
  assert.deepEqual(warnings, ['Invalid timezone "Nowhere/Imaginary", falling back to UTC']);
});

test('an unresolvable zone falls back to UTC bounds', () => {
  const bounds = getDayBoundsInTimezone(new Date('2026-08-08T02:00:00Z'), 'Nowhere/Imaginary');
  assert.equal(bounds.start.toISOString(), '2026-08-08T00:00:00.000Z');
  assert.equal(bounds.end.toISOString(), '2026-08-08T23:59:59.999Z');
});

test('formatTimestamp warns once for an unresolvable schedule zone over a full preview', () => {
  const warnings = collectWarnings(() => {
    for (let day = 0; day < PREVIEW_DAYS; day++) {
      formatTimestamp(new Date(Date.UTC(2026, 0, 1) + day * DAY_MS), {
        useLocalTimezone: false,
        useUtc: false,
        timeZone: 'Nowhere/AlsoImaginary',
        use24Hour: true,
        forceYear: false
      });
    }
  });
  assert.deepEqual(warnings, ['Invalid timezone "Nowhere/AlsoImaginary", falling back to UTC']);
});

test('a moment past midnight UTC bounds the UTC day it falls in', () => {
  const bounds = getDayBoundsInTimezone(new Date('2026-08-08T02:00:00Z'), 'UTC');
  assert.equal(bounds.start.toISOString(), '2026-08-08T00:00:00.000Z');
  assert.equal(bounds.end.toISOString(), '2026-08-08T23:59:59.999Z');
});

// The same instant is still August 7 in Denver, and this is the case the browser's own calendar
// used to decide: an event labelled August 8 on the UTC clock selected August 7.
test('the same moment bounds the previous day in a zone behind UTC', () => {
  const bounds = getDayBoundsInTimezone(new Date('2026-08-08T02:00:00Z'), 'America/Denver');
  assert.equal(bounds.start.toISOString(), '2026-08-07T06:00:00.000Z');
  assert.equal(bounds.end.toISOString(), '2026-08-08T05:59:59.999Z');
});

test('the same moment bounds the next day in a zone ahead of UTC', () => {
  const bounds = getDayBoundsInTimezone(new Date('2026-08-08T22:00:00Z'), 'Asia/Tokyo');
  assert.equal(bounds.start.toISOString(), '2026-08-08T15:00:00.000Z');
  assert.equal(bounds.end.toISOString(), '2026-08-09T14:59:59.999Z');
});

test('the last moment of a day and the first of the next never land on the same day', () => {
  const zone = 'America/Denver';
  const bounds = getDayBoundsInTimezone(new Date('2026-08-07T12:00:00Z'), zone);
  const nextDay = getDayBoundsInTimezone(new Date(bounds.end.getTime() + 1), zone);
  assert.equal(nextDay.start.getTime(), bounds.end.getTime() + 1);
  assert.equal(bounds.end.getTime() - bounds.start.getTime(), DAY_MS - 1);
});

test('a spring-forward day is 23 hours long, not 24', () => {
  const bounds = getDayBoundsInTimezone(new Date('2026-03-08T18:00:00Z'), 'America/Denver');
  assert.equal(bounds.start.toISOString(), '2026-03-08T07:00:00.000Z');
  assert.equal(bounds.end.getTime() - bounds.start.getTime(), 23 * 60 * 60 * 1000 - 1);
});

test('a fall-back day is 25 hours long, not 24', () => {
  const bounds = getDayBoundsInTimezone(new Date('2026-11-01T18:00:00Z'), 'America/Denver');
  assert.equal(bounds.start.toISOString(), '2026-11-01T06:00:00.000Z');
  assert.equal(bounds.end.getTime() - bounds.start.getTime(), 25 * 60 * 60 * 1000 - 1);
});

test('the day after December 31 rolls into the next year', () => {
  const zone = 'America/Denver';
  const bounds = getDayBoundsInTimezone(new Date('2026-12-31T18:00:00Z'), zone);
  assert.equal(bounds.start.toISOString(), '2026-12-31T07:00:00.000Z');
  assert.equal(bounds.end.toISOString(), '2027-01-01T06:59:59.999Z');
  assert.equal(getDayStartInTimezone(2027, 0, 1, zone).getTime(), bounds.end.getTime() + 1);
});

test('a day number past the end of the month is the day in the following month', () => {
  const zone = 'UTC';
  assert.equal(getDayStartInTimezone(2026, 1, 29, zone).toISOString(), '2026-03-01T00:00:00.000Z');
});

// The quick presets in the date picker count backwards with plain arithmetic on these fields, so
// day 0, a negative day and a month either side of the year have to land where a calendar says.
test('day zero is the last day of the previous month', () => {
  assert.equal(getDayStartInTimezone(2026, 8, 0, 'UTC').toISOString(), '2026-08-31T00:00:00.000Z');
});

test('counting six days back from the first of a month crosses into the previous one', () => {
  assert.equal(
    getDayStartInTimezone(2026, 8, 1 - 6, 'UTC').toISOString(),
    '2026-08-26T00:00:00.000Z'
  );
});

test('the month before January is December of the previous year', () => {
  assert.equal(getDayStartInTimezone(2026, -1, 1, 'UTC').toISOString(), '2025-12-01T00:00:00.000Z');
});

test('day zero of January is the last day of the previous year', () => {
  assert.equal(getDayStartInTimezone(2026, 0, 0, 'UTC').toISOString(), '2025-12-31T00:00:00.000Z');
});
