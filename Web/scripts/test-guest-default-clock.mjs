import assert from 'node:assert/strict';
import test from 'node:test';
import { TIME_SETTING_VALUES } from '../src/contexts/TimezoneContext.types.ts';
import { clockFromTimeSetting, timeSettingFromClock } from '../src/utils/pendingPreferences.ts';
import {
  applyGuestClockChanges,
  shouldApplyGuestClockChange,
  shouldApplyGuestDefaultChange
} from '../src/utils/guestDefaultPreferenceGate.ts';

const defaults = (clock) => ({
  ...clock,
  sharpCorners: false,
  disableTooltips: false,
  showDatasourceLabels: true,
  allowedTimeFormats: [...TIME_SETTING_VALUES]
});

const session = (clock) => ({ selectedTheme: null, ...defaults(clock) });

test('every offered clock survives a trip through the flags and back', () => {
  for (const value of TIME_SETTING_VALUES) {
    assert.equal(timeSettingFromClock(clockFromTimeSetting(value)), value, value);
  }
});

test('choosing UTC also settles the 12/24 question', () => {
  assert.deepEqual(clockFromTimeSetting('utc'), {
    useUtcTimezone: true,
    useLocalTimezone: false,
    use24HourFormat: true
  });
});

test('UTC names the clock even with the local flag left on behind it', () => {
  assert.equal(
    timeSettingFromClock({
      useUtcTimezone: true,
      useLocalTimezone: true,
      use24HourFormat: false
    }),
    'utc'
  );
});

test('a guest still on the old default adopts the new one', () => {
  const previousDefaults = defaults(clockFromTimeSetting('server-12h'));
  const guest = session(clockFromTimeSetting('server-12h'));

  assert.equal(shouldApplyGuestClockChange(guest, previousDefaults), true);
});

// The defect: the shared default cache was advanced to the new clock by whichever listener the hub
// called first, so this same guest was compared against the clock it was being moved TO and was
// judged to have chosen it deliberately. Passing the tuple the change replaced is what fixes it.
test('the old default, not the new one, decides whether a guest inherits', () => {
  const guest = session(clockFromTimeSetting('server-12h'));
  const alreadyAdvanced = defaults(clockFromTimeSetting('utc'));
  const asItWas = defaults(clockFromTimeSetting('server-12h'));

  assert.equal(shouldApplyGuestClockChange(guest, alreadyAdvanced), false);
  assert.equal(shouldApplyGuestClockChange(guest, asItWas), true);
});

// The three flags are one clock, so they are judged as one. A guest reading UTC by its own choice
// happens to share useLocalTimezone with a server-clock default; taking that one field on its own
// left it on UTC with the local flag on, which is a clock no option offers.
test('a guest that picked its own clock keeps every flag of it', () => {
  const guest = session(clockFromTimeSetting('utc'));
  const previousDefaults = defaults(clockFromTimeSetting('server-12h'));

  assert.equal(
    shouldApplyGuestDefaultChange('useLocalTimezone', guest, previousDefaults),
    true,
    'one field on its own still matches'
  );
  assert.equal(shouldApplyGuestClockChange(guest, previousDefaults), false);
});

test('clock changes received during the preference load replay in order', () => {
  const loadedGuest = session(clockFromTimeSetting('server-12h'));
  const changes = [
    {
      previousClock: clockFromTimeSetting('server-12h'),
      clock: clockFromTimeSetting('local-24h')
    },
    {
      previousClock: clockFromTimeSetting('local-24h'),
      clock: clockFromTimeSetting('utc')
    }
  ];

  assert.deepEqual(
    applyGuestClockChanges(loadedGuest, changes),
    session(clockFromTimeSetting('utc'))
  );
});
