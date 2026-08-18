import assert from 'node:assert/strict';
import test from 'node:test';
import { setTimeout as delay } from 'node:timers/promises';
import { compileToUrl } from './transpile-module.mjs';

/**
 * The clock a plain module reads and the clock the timezone context shows have to be the same
 * clock. Each module is compiled once and its own address substituted into whatever imports it, so
 * the pending entries the test writes are the entries the readers read.
 */

const pendingUrl = await compileToUrl('../src/utils/pendingPreferences.ts', {
  '../types/userPreferences.ts': await compileToUrl('../src/types/userPreferences.ts')
});
const globalPreferenceUrl = await compileToUrl('../src/utils/globalPreference.ts');
const nested = {
  './globalPreference': globalPreferenceUrl,
  './pendingPreferences': pendingUrl
};
const localUrl = await compileToUrl('../src/utils/timezonePreference.ts', nested);
const utcUrl = await compileToUrl('../src/utils/utcTimezonePreference.ts', nested);

const {
  setPendingTimezone,
  dropPendingTimezone,
  confirmPendingTimezone,
  preferPendingTimezone,
  clockFromTimeSetting,
  timeSettingFromClock,
  getPendingValue,
  subscribe
} = await import(pendingUrl);
const { setGlobalTimezonePreference } = await import(localUrl);
const { setGlobalUtcPreference } = await import(utcUrl);
const { getEffectiveTimezone, getLocalTimezone, setServerTimezone } = await import(
  await compileToUrl('../src/utils/timezone.ts', {
    './timezonePreference': localUrl,
    './utcTimezonePreference': utcUrl
  })
);

const SERVER_ZONE = 'Europe/Berlin';

/**
 * No click outstanding, on the server clock, which is what a fresh page load looks like. A pick
 * dropped the moment it is made empties the map whatever was in it, because its three keys are
 * every key there is.
 */
const atRest = () => {
  dropPendingTimezone(setPendingTimezone('server-12h'));
  setGlobalTimezonePreference(false);
  setGlobalUtcPreference(false);
  setServerTimezone(SERVER_ZONE);
};

/** The click, with no save behind it. Hands back the number a rollback needs. */
const click = (value) => {
  atRest();
  return setPendingTimezone(value);
};

/** The clock stored behind these tests, which is what atRest puts the readers back on. */
const STORED_CLOCK = {
  useUtcTimezone: false,
  useLocalTimezone: false,
  use24HourFormat: false
};

/** The setting the selector names, resolved the way TimezoneContext resolves it for the control. */
const clockOnScreen = () =>
  timeSettingFromClock({
    useUtcTimezone: getPendingValue('useUtcTimezone') ?? STORED_CLOCK.useUtcTimezone,
    useLocalTimezone: getPendingValue('useLocalTimezone') ?? STORED_CLOCK.useLocalTimezone,
    use24HourFormat: getPendingValue('use24HourFormat') ?? STORED_CLOCK.use24HourFormat
  });

/** What the server echoing the save back does: the confirmed values catch up to the click. */
const saveEchoes = (value) => {
  const clock = clockFromTimeSetting(value);
  setGlobalTimezonePreference(clock.useLocalTimezone);
  setGlobalUtcPreference(clock.useUtcTimezone);
};

test('a page with no click outstanding reads the confirmed clock', () => {
  atRest();
  assert.equal(getEffectiveTimezone(), SERVER_ZONE);
});

test('the clock moves on the click, before any save', () => {
  click('utc');
  assert.equal(getEffectiveTimezone(), 'UTC');

  click('local-24h');
  assert.equal(getEffectiveTimezone(), getLocalTimezone());
});

test('the save echoing back changes nothing the click had not already changed', () => {
  click('utc');
  saveEchoes('utc');
  assert.equal(getEffectiveTimezone(), 'UTC');
});

test('a failed save puts the clock back where it was', () => {
  const utc = click('utc');
  assert.equal(getEffectiveTimezone(), 'UTC');

  dropPendingTimezone(utc);
  assert.equal(
    getEffectiveTimezone(),
    SERVER_ZONE,
    'the click must not have been written anywhere the rollback cannot reach'
  );
});

test('a failed save does not take back the click that came after it', () => {
  const utc = click('utc');
  setPendingTimezone('local-24h');

  dropPendingTimezone(utc);
  assert.equal(getEffectiveTimezone(), getLocalTimezone());
});

/**
 * The clicks carry the same value, so nothing about the entries themselves separates the first from
 * the third. Only the pick each entry was written by can, which is why the rollback names a pick
 * instead of a setting.
 */
test('a failed save does not take back a later click that picked the same setting', () => {
  const first = click('utc');
  setPendingTimezone('local-12h');
  setPendingTimezone('utc');

  dropPendingTimezone(first);
  assert.equal(
    clockOnScreen(),
    'utc',
    'the click still being saved has to survive an older one failing on the same setting'
  );
});

test('a preference change from another device moves the clock', () => {
  atRest();
  saveEchoes('utc');
  assert.equal(getEffectiveTimezone(), 'UTC');
});

test('a preference change from another device loses to the click still in flight', () => {
  click('local-24h');
  saveEchoes('utc');
  assert.equal(
    getEffectiveTimezone(),
    getLocalTimezone(),
    'the older value must not overtake the click the reader is waiting on'
  );
});

test('an argument passed outright still wins over both', () => {
  click('utc');
  assert.equal(getEffectiveTimezone(true, false), getLocalTimezone());
  assert.equal(getEffectiveTimezone(false, false), SERVER_ZONE);
});

/**
 * The three flags name one clock between them, so a click has to move as one event. Taken up or
 * back one flag at a time they are three notifications and the control repaints on each: two flags
 * from the click beside one still on the stored value spell a third setting, and that is one nobody
 * picked.
 */
test('a click and the rollback behind it never show a clock between the two', () => {
  atRest();
  const seen = [];
  const unsubscribe = subscribe(() => seen.push(clockOnScreen()));

  dropPendingTimezone(setPendingTimezone('utc'));
  unsubscribe();

  assert.deepEqual(
    [...new Set(seen)],
    ['utc', 'server-12h'],
    'every repaint has to show either the clock that was picked or the one that was stored'
  );
});

/**
 * A save slower than a wall-clock deadline used to lose the click to it: the entries went, every
 * reader slid back to the stored clock, and the save then landed and moved them forward again. The
 * save is what the pick waits on now, so the pick outlives any wait.
 */
test('a click still waiting on its save keeps the clock it picked', async () => {
  atRest();
  setPendingTimezone('utc');

  await delay(2400);

  assert.equal(
    clockOnScreen(),
    'utc',
    'a save that is still running has not disagreed with the click, so nothing may take it back'
  );
  assert.equal(getEffectiveTimezone(), 'UTC');
});

/**
 * The save answering yes is what ends a pick, and it has to end it outright. Held past that the
 * pick would go on outranking every later change for as long as the tab is open, so the next clock
 * set from another device or by an administrator would never reach this reader.
 */
test('the save answering yes settles a pick the readers already hold, and tells them', () => {
  atRest();
  const utc = setPendingTimezone('utc');
  const resolved = preferPendingTimezone(false, true, true);

  let announcements = 0;
  const unsubscribe = subscribe(() => {
    announcements += 1;
  });

  confirmPendingTimezone(utc);
  unsubscribe();

  assert.deepEqual(resolved, { useLocal: false, useUtc: true, use24Hour: true });
  assert.equal(
    getPendingValue('useUtcTimezone'),
    null,
    'a pick the server has confirmed must stop outranking what comes after it'
  );
  assert.ok(announcements > 0, 'the readers have no other way to learn the pick is settled');
});

/**
 * Two picks of the same setting write the same three values, so nothing in the entries tells the
 * first from the second. An update carrying those values back says the readers have caught up; it
 * does not say which pick they caught up with, and reading it as the second pick's own answer lets
 * a broadcast older than that pick overtake a click that is still on the wire.
 */
test('an update carrying the values back does not settle a click still on the wire', () => {
  atRest();
  setPendingTimezone('utc');
  setPendingTimezone('utc');

  preferPendingTimezone(false, true, true);
  preferPendingTimezone(false, false, false);

  assert.equal(
    clockOnScreen(),
    'utc',
    'the click still being saved has to survive an older broadcast that followed its own echo'
  );
});

/**
 * The socket is down across the save, so the server took the pick and no update will ever carry it
 * back. The readers hold what the server is holding, because the pick is what the server is
 * holding. Released on the answer alone they would go back to the clock the pick replaced and stay
 * there for as long as the tab lives, with the server storing the other one.
 */
test('a saved pick with no echo behind it leaves the readers on the clock that was saved', () => {
  atRest();
  confirmPendingTimezone(setPendingTimezone('utc'));

  assert.equal(clockOnScreen(), 'utc', 'the reader must not be put back on the clock it left');
  assert.equal(getEffectiveTimezone(), 'UTC');
});

/**
 * Held until the readers have something newer, and no longer: a pick released on nothing but the
 * echo is held for the life of the tab whenever that echo is lost, and outranks every change that
 * follows it.
 */
test('a saved pick whose echo never came releases on the next change', () => {
  atRest();
  const utc = setPendingTimezone('utc');
  confirmPendingTimezone(utc);

  const resolved = preferPendingTimezone(true, false, true);

  assert.deepEqual(resolved, { useLocal: true, useUtc: false, use24Hour: true });
  assert.equal(
    getPendingValue('useUtcTimezone'),
    null,
    'a saved pick must not outlive the change that overtakes it'
  );
});

/**
 * The echo that raced the click carries the old value for one flag and the new one for the other
 * two. Settling on a partial match would hand that one flag straight back to the value the reader
 * has just left, which is the whole reason the pick is held.
 */
test('a broadcast older than the click neither wins nor settles it', () => {
  atRest();
  setPendingTimezone('utc');

  const resolved = preferPendingTimezone(false, false, true);

  assert.deepEqual(resolved, { useLocal: false, useUtc: true, use24Hour: true });
  assert.equal(getPendingValue('useUtcTimezone'), true, 'the click has to survive the stale echo');
  assert.equal(clockOnScreen(), 'utc');
});
