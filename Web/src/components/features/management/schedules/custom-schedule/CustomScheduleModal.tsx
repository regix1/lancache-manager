import { noAutofill } from '@utils/autofill';
import React, { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronRight } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { Modal } from '@components/ui/Modal';
import { MultiSelectDropdown, type MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import { NumberInput } from '@components/ui/NumberInput';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { prefersReducedMotion } from '@components/features/management/status-check/helpers';
import { useReaderClock } from '@hooks/useReaderClock';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import { getEffectiveTimezone, getLocalTimezone, getServerTimezone } from '@utils/timezone';
import {
  CRON_POSITIONS,
  DEFAULT_SCHEDULE_DRAFT,
  buildExpression,
  computeNextRuns,
  describeSchedule,
  expressionError,
  fillsPosition,
  formatTimeUntil,
  isSameSchedule,
  invalidPositions,
  isWeekdayKey,
  joinExpression,
  readDraft,
  readSchedule,
  splitExpression,
  toCustomSchedule,
  toTwelveHour,
  toTwentyFourHour
} from './customSchedulePreview';
import { listZoneRegions, zoneOffsetLabel, type ZoneRegion } from './scheduleZones';
import {
  SCHEDULE_REPEATS,
  WEEKDAY_KEYS,
  type ClockTime,
  type CustomSchedule,
  type ScheduleDraft,
  type ScheduleRepeat
} from './types';
import './CustomScheduleModal.css';

const BASE_KEY = 'management.schedules.customSchedule';
const PREVIEW_RUN_COUNT = 5;
/** A wall-clock label carries no date, so any fixed day works as its carrier. */
const CLOCK_CARRIER_DAY = new Date(2000, 0, 1);

/** Where the caret lands in a cron box that focus was just moved to. */
type CaretPlacement = 'start' | 'end' | 'all';

/** Offered on its own because a schedule that must not move with the seasons is written in it. */
const UTC_ZONE = 'UTC';

interface CustomScheduleModalProps {
  opened: boolean;
  /** The saved schedule to edit, or null to build a new one. */
  schedule: CustomSchedule | null;
  isDisabled?: boolean;
  isSaving?: boolean;
  /** A rejection sentence from the server, shown at the top of the body. */
  errorMessage?: string | null;
  onClose: () => void;
  onApply: (schedule: CustomSchedule) => void;
}

interface TimeFieldsProps {
  label: string;
  time: ClockTime;
  disabled: boolean;
  /** False puts the hour on a 12-hour face with an AM/PM control beside it. */
  use24Hour: boolean;
  /** Drops the hour stepper for a repeat that has no hour of its own, leaving the minute. */
  minuteOnly?: boolean;
  onChange: (time: ClockTime) => void;
}

/**
 * Hour and minute as a pair of steppers, on whichever clock face the modal is set to. The
 * value handed back is always on the 24-hour clock: cron needs 0-23 and the wire shape is
 * "HH:mm", so the 12-hour face is converted at this edge and never stored.
 */
function TimeFields({
  label,
  time,
  disabled,
  use24Hour,
  minuteOnly = false,
  onChange
}: TimeFieldsProps): React.ReactElement {
  const { t } = useTranslation();
  const twelve = toTwelveHour(time.hour);

  const periodOptions = useMemo(
    () => [
      { value: 'am', label: t('common.dateTimePicker.am') },
      { value: 'pm', label: t('common.dateTimePicker.pm') }
    ],
    [t]
  );

  const handleHourChange = useCallback(
    (hour: number): void => {
      onChange({ ...time, hour: use24Hour ? hour : toTwentyFourHour(hour, twelve.period) });
    },
    [onChange, time, twelve.period, use24Hour]
  );

  const handlePeriodChange = useCallback(
    (value: string): void => {
      if (value !== 'am' && value !== 'pm') return;
      onChange({ ...time, hour: toTwentyFourHour(twelve.hour, value) });
    },
    [onChange, time, twelve.hour]
  );

  return (
    <div className="custom-schedule-time" role="group" aria-label={label}>
      {!minuteOnly && (
        <>
          <div className="custom-schedule-time-part">
            <NumberInput
              value={use24Hour ? time.hour : twelve.hour}
              min={use24Hour ? 0 : 1}
              max={use24Hour ? 23 : 12}
              disabled={disabled}
              aria-label={label}
              onChange={handleHourChange}
            />
          </div>
          {/* The separator only means anything between two halves of a clock time. On its own
              in front of a lone minute it reads as a broken time, so it goes with the hour. */}
          <span className="custom-schedule-time-separator" aria-hidden="true">
            :
          </span>
        </>
      )}
      <div className="custom-schedule-time-part">
        <NumberInput
          value={time.minute}
          min={0}
          max={59}
          disabled={disabled}
          aria-label={label}
          onChange={(minute) => onChange({ ...time, minute })}
        />
      </div>
      {!minuteOnly && !use24Hour && (
        <SegmentedControl
          options={periodOptions}
          value={twelve.period}
          onChange={handlePeriodChange}
          size="md"
          showLabels
          fullWidth
          className="custom-schedule-period"
        />
      )}
    </div>
  );
}

function CustomScheduleModal({
  opened,
  schedule,
  isDisabled = false,
  isSaving = false,
  errorMessage = null,
  onClose,
  onApply
}: CustomScheduleModalProps): React.ReactElement {
  const { t } = useTranslation();
  const readerClock = useReaderClock();
  const fieldId = useId();

  const [draft, setDraft] = useState<ScheduleDraft>(DEFAULT_SCHEDULE_DRAFT);
  /** Set only while a hand-typed expression is in play; null means the builder is in charge. */
  const [manualExpression, setManualExpression] = useState<string | null>(null);
  /**
   * The five positions exactly as typed, held apart from the joined expression because the
   * join and the re-split are not inverses: clearing a middle box leaves two spaces, which
   * splits back into four positions and shifts every later one left under the user's cursor.
   * Null means nobody has typed in the boxes, so they read from the expression instead.
   */
  const [typedPositions, setTypedPositions] = useState<string[] | null>(null);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  // The zone the whole schedule is read in. It starts as the server's own, which is what every
  // schedule written before this control existed carries, and a saved schedule reopens in the
  // zone it was saved with rather than in whatever the server happens to be set to now.
  const [timeZoneId, setTimeZoneId] = useState<string>(getServerTimezone);
  // A view setting for this modal only, seeded from the global clock preference. Writing back
  // to the global one would change how every timestamp in the app renders, which is not what
  // someone switching the face on a time field is asking for.
  const [use24Hour, setUse24Hour] = useState(readerClock.use24Hour);
  // Anchoring the preview to the moment the modal opened keeps the five rows still while the
  // user works, instead of every keystroke re-reading the clock and shifting the list.
  const [previewFrom, setPreviewFrom] = useState<number>(() => Date.now());

  // Read through a ref so a caller passing a fresh object literal each render cannot restart
  // the form underneath the user.
  const scheduleRef = useRef<CustomSchedule | null>(schedule);
  scheduleRef.current = schedule;

  // Read through a ref for the same reason: the global preference must seed the local face on
  // open without re-running this effect and resetting the form when it changes elsewhere.
  const clockPreferenceRef = useRef(readerClock.use24Hour);
  clockPreferenceRef.current = readerClock.use24Hour;

  useEffect(() => {
    if (!opened) return;
    const saved = scheduleRef.current;
    const restored = saved ? readSchedule(saved) : null;
    setDraft(restored ?? DEFAULT_SCHEDULE_DRAFT);
    setManualExpression(saved && !restored ? saved.expression : null);
    setTypedPositions(null);
    setAdvancedOpen(false);
    setUse24Hour(clockPreferenceRef.current);
    setTimeZoneId(saved?.timeZoneId || getServerTimezone());
    setPreviewFrom(Date.now());
  }, [opened]);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const advancedRef = useRef<HTMLDivElement | null>(null);

  // The drawer opens below the fold of the scroll area, so without this it appears to do nothing
  // until something else scrolls. The distance is only final once the region has finished growing,
  // which is why it is measured after the expand rather than on the state flip. The wait covers the
  // region's 0.35s grid-template-rows growth (collapsible.css) with headroom.
  useEffect(() => {
    if (!advancedOpen) return;
    const timer = setTimeout(() => {
      const scroller = scrollRef.current;
      const advanced = advancedRef.current;
      if (!scroller || !advanced) return;
      const delta =
        advanced.getBoundingClientRect().bottom - scroller.getBoundingClientRect().bottom;
      if (delta <= 0) return;
      scroller.scrollTo({
        top: scroller.scrollTop + delta,
        behavior: prefersReducedMotion() ? 'auto' : 'smooth'
      });
    }, 400);
    return () => clearTimeout(timer);
  }, [advancedOpen]);

  const serverZone = getServerTimezone();
  // The machine's own zone, for the quick pick that offers it. Never the display setting: with
  // the app-wide UTC setting on, that reads UTC and the row would name a zone nobody is in.
  const browserZone = getLocalTimezone();
  // The zone the preview rows are actually rendered in, which is the reader's display setting:
  // their browser's zone, the server's, or UTC. This is what the note underneath compares
  // against the schedule's own zone, because it is what those rows are written in. The UTC
  // setting is read off the context rather than left to the module state inside
  // getEffectiveTimezone, so switching it repaints this modal along with everything else.
  const displayZone = getEffectiveTimezone(readerClock.useLocalTimezone, readerClock.useUtc);
  const previewStart = useMemo(() => new Date(previewFrom), [previewFrom]);

  const formatClock = useCallback(
    (time: ClockTime): string => {
      const carrier = new Date(CLOCK_CARRIER_DAY);
      carrier.setHours(time.hour, time.minute, 0, 0);
      return formatTimestamp(carrier, {
        // The carrier is built in browser-local time and reads as a wall clock, so it must
        // not be shifted into another zone on the way out. That includes the app-wide UTC
        // setting, which would otherwise move a label that is not an instant at all.
        useLocalTimezone: true,
        useUtc: false,
        use24Hour,
        forceYear: false,
        style: 'timeOnly'
      });
    },
    [use24Hour]
  );

  const expression = manualExpression ?? buildExpression(draft);
  /** What the boxes hold on a fresh modal, and what the reset button puts back: "0 0 * * *". */
  const defaultExpression = buildExpression(DEFAULT_SCHEDULE_DRAFT);
  const derivedPositions = useMemo((): string[] => splitExpression(expression), [expression]);
  const expressionPositions = typedPositions ?? derivedPositions;
  const isDefaultExpression = joinExpression(expressionPositions) === defaultExpression;
  const builderLocked = manualExpression !== null && readDraft(manualExpression) === null;
  const needsDay = !builderLocked && draft.repeat === 'weekly' && draft.weekdays.length === 0;
  const parseError = needsDay ? null : expressionError(expression, timeZoneId);

  const candidate = useMemo(
    (): CustomSchedule => toCustomSchedule(draft, expression, timeZoneId),
    [draft, expression, timeZoneId]
  );

  const nextRuns = useMemo(
    (): Date[] =>
      needsDay || parseError !== null
        ? []
        : computeNextRuns(candidate, PREVIEW_RUN_COUNT, previewStart),
    [candidate, needsDay, parseError, previewStart]
  );

  const neverRuns = !needsDay && parseError === null && nextRuns.length === 0;
  /**
   * A window whose two bounds are the same instant. It is the one pair of window times that
   * cannot work: every other pair is a real span, including an end EARLIER than the start,
   * which is how a window that runs overnight is written. It has its own message because the
   * general one blames the run time for falling outside the window, and here the run time is
   * blameless - no time at all falls inside a window of zero length.
   */
  const windowIsZeroLength =
    draft.windowEnabled &&
    draft.windowStart.hour === draft.windowEnd.hour &&
    draft.windowStart.minute === draft.windowEnd.minute;
  const hasChanges = !isSameSchedule(candidate, schedule);
  const canSave =
    !isDisabled && !isSaving && !needsDay && parseError === null && !neverRuns && hasChanges;

  const stampSettings: TimestampSettings = {
    ...readerClock,
    // Read off the context for the same reason displayZone is: the note below decides whether
    // to appear by comparing displayZone against the schedule's zone, so its text has to be
    // written on that same clock. The module state lags the switch by a save round trip, which
    // would leave the note visible on the new setting and printed in the old one.
    // The whole modal speaks one clock, so the preview rows follow the modal's own face
    // rather than the global preference the face was seeded from.
    use24Hour,
    forceYear: false
  };

  /**
   * The runs, written in the schedule's OWN zone. Every other field in this modal is: the time
   * steppers, the cron boxes, the sentence. Rendering only this list in the reader's display
   * zone meant typing 05:55 and being answered "12:55 AM", two numbers for one moment with
   * nothing on screen tying them together. The reader's own time is still given, once, below.
   */
  const scheduleStampSettings: TimestampSettings = { ...stampSettings, timeZone: timeZoneId };

  const weekdayOptions = useMemo(
    (): MultiSelectOption[] =>
      WEEKDAY_KEYS.map((day) => ({ value: day, label: t(`${BASE_KEY}.weekdays.${day}`) })),
    [t]
  );

  const repeatOptions = useMemo(
    () =>
      SCHEDULE_REPEATS.map((repeat) => ({
        value: repeat,
        label: t(`${BASE_KEY}.repeat.${repeat}`)
      })),
    [t]
  );

  const updateDraft = useCallback((patch: Partial<ScheduleDraft>): void => {
    // Touching any builder control hands control back to it, which is what makes the
    // Advanced field a deliberate detour rather than a second source of truth.
    setManualExpression(null);
    setTypedPositions(null);
    setDraft((current) => ({ ...current, ...patch }));
  }, []);

  const handleRepeatChange = useCallback((value: string): void => {
    const repeat: ScheduleRepeat | undefined = SCHEDULE_REPEATS.find((option) => option === value);
    if (!repeat) return;
    setManualExpression(null);
    setTypedPositions(null);
    // The hour is carried across every shape unchanged. The hourly shape cannot use it, and
    // three separate things already keep it out of that shape's output: the expression it
    // builds has no hour position, its sentence reads the minute and never the time, and its
    // hour stepper is not rendered. Zeroing it here as well only threw away the hour the other
    // three shapes need, so a user who looked at Hourly and changed their mind lost it.
    setDraft((current) => ({ ...current, repeat }));
  }, []);

  const handleWeekdaysChange = useCallback(
    (values: string[]): void => {
      updateDraft({ weekdays: values.filter(isWeekdayKey) });
    },
    [updateDraft]
  );

  const applyPositions = useCallback((next: string[]): void => {
    const rebuilt = joinExpression(next);
    setTypedPositions(next);
    setManualExpression(rebuilt);
    const parsed = readDraft(rebuilt);
    // A hand-typed expression the controls CAN express keeps them in step; one they cannot
    // is left exactly as typed and the controls stand down.
    if (parsed) {
      setDraft((current) => ({
        ...parsed,
        windowEnabled: current.windowEnabled,
        windowStart: current.windowStart,
        windowEnd: current.windowEnd
      }));
    }
  }, []);

  const positionInputs = useRef<(HTMLInputElement | null)[]>([]);

  /**
   * Puts the caret in another position's box. `all` selects what is already there so the next
   * keystroke replaces it, which is what an arriving caret wants: the box it lands on usually
   * holds a `*` or a number the builder wrote, not something to type onto the end of.
   */
  const focusPosition = useCallback((index: number, caret: CaretPlacement): void => {
    const input = positionInputs.current[index];
    if (!input) return;
    input.focus();
    if (caret === 'all') {
      input.select();
      return;
    }
    const at = caret === 'start' ? 0 : input.value.length;
    input.setSelectionRange(at, at);
  }, []);

  const handlePositionChange = useCallback(
    (index: number, event: React.ChangeEvent<HTMLInputElement>): void => {
      // A cron position is never longer than a short list or step, and a space here would
      // silently turn one position into two.
      const cleaned = event.target.value.replace(/\s+/g, '');
      applyPositions(expressionPositions.map((current, at) => (at === index ? cleaned : current)));
    },
    [applyPositions, expressionPositions]
  );

  const handlePositionKeyDown = useCallback(
    (index: number, event: React.KeyboardEvent<HTMLInputElement>): void => {
      if (event.altKey || event.ctrlKey || event.metaKey) return;
      const input = event.currentTarget;
      if (event.key === ' ') {
        // A space is how the expression itself separates positions, and one typed into a box is
        // stripped anyway, so it moves to the next box rather than doing nothing at all.
        event.preventDefault();
        focusPosition(index + 1, 'all');
        return;
      }
      if (event.key === 'Tab') {
        // Tab is moved by hand rather than left to the document's own order. The boxes sit in a
        // portalled modal behind a focus trap, and whether the browser's next stop is the box
        // beside this one depends on where focus happened to be when the modal opened; typing
        // five numbers must not depend on that. Only steps INSIDE the group are taken, so Tab
        // off the last box and Shift+Tab off the first still leave for the buttons as usual.
        const next = event.shiftKey ? index - 1 : index + 1;
        if (next >= 0 && next < CRON_POSITIONS.length) {
          event.preventDefault();
          focusPosition(next, 'all');
        }
        return;
      }
      const caretAt = input.selectionStart;
      const hasSelection = input.selectionStart !== input.selectionEnd;
      // A number that fills its position hands the box over to the next one, so all five can be
      // typed without reaching for Tab in between. It waits for the following digit to do it,
      // because an hour of 3 is finished only if nothing follows: 3,9, 3-5 and 3/2 all carry on
      // from it, and moving on the 3 itself takes the box away mid-expression. A digit is the one
      // key that cannot belong here, since a second one would put the number out of range. The
      // key is left to the browser rather than cancelled, so it lands in the box that arrives.
      if (
        /^\d$/.test(event.key) &&
        index + 1 < CRON_POSITIONS.length &&
        !hasSelection &&
        caretAt === input.value.length &&
        fillsPosition(index, input.value)
      ) {
        focusPosition(index + 1, 'all');
        return;
      }
      if (event.key === 'Backspace' && input.value.length === 0) {
        event.preventDefault();
        focusPosition(index - 1, 'end');
        return;
      }
      if (event.key === 'ArrowLeft' && !hasSelection && caretAt === 0) {
        event.preventDefault();
        focusPosition(index - 1, 'end');
        return;
      }
      if (event.key === 'ArrowRight' && !hasSelection && caretAt === input.value.length) {
        event.preventDefault();
        focusPosition(index + 1, 'start');
      }
    },
    [focusPosition]
  );

  /**
   * Puts the five boxes back to the daily-at-midnight expression the modal opens on, and hands
   * the controls above back with them: a reset that left the builder standing down would leave
   * the panel saying the expression cannot be represented while showing one that plainly can be.
   * The window is not touched, since it is not part of the expression this button is under.
   */
  const handleResetExpression = useCallback((): void => {
    setManualExpression(null);
    setTypedPositions(null);
    setDraft((current) => ({
      ...DEFAULT_SCHEDULE_DRAFT,
      windowEnabled: current.windowEnabled,
      windowStart: current.windowStart,
      windowEnd: current.windowEnd
    }));
    focusPosition(0, 'all');
  }, [focusPosition]);

  const handlePositionPaste = useCallback(
    (index: number, event: React.ClipboardEvent<HTMLInputElement>): void => {
      // Splitting the boxes out costs the one person who already has an expression on their
      // clipboard the ability to drop it in, so a paste carrying more than one position fills
      // the boxes from this one onward instead of stuffing all of it into a single box.
      const parts = event.clipboardData
        .getData('text')
        .trim()
        .split(/\s+/)
        .filter((part) => part.length > 0);
      if (parts.length < 2) return;
      event.preventDefault();
      applyPositions(
        expressionPositions.map((current, at) =>
          at >= index && at - index < parts.length ? parts[at - index] : current
        )
      );
    },
    [applyPositions, expressionPositions]
  );

  const handleApply = useCallback((): void => {
    if (!canSave) return;
    onApply(candidate);
  }, [canSave, candidate, onApply]);

  // The hourly shape fires at the same minute past every Nth hour, so its field is a minute
  // rather than a clock time and says so instead of labelling a lone number "Time".
  const timeFieldLabel = t(
    `${BASE_KEY}.repeat.${draft.repeat === 'hourly' ? 'atMinute' : 'atTime'}`
  );
  // A schedule in the server's own zone still says so; one in a zone of its own names both, so
  // nobody has to work out from a bare zone id whether the server was overridden or not.
  const zoneNote =
    timeZoneId === serverZone
      ? t(`${BASE_KEY}.timezone.server`, { zone: timeZoneId })
      : t(`${BASE_KEY}.timezone.custom`, { zone: timeZoneId, serverZone });
  const sentence = describeSchedule(draft, timeZoneId, t, formatClock);
  // The same run on the reader's own clock, for a schedule that fires in some other zone. Given
  // once rather than on every row: five stamps twice over is a table, and the question it answers
  // ("when is that for me") is asked once.
  const nextRunHere =
    nextRuns.length > 0 && displayZone !== timeZoneId
      ? formatTimestamp(nextRuns[0], stampSettings)
      : null;
  // No UTC line beside it. It was worth having when UTC was the only stable reference this modal
  // could offer; a schedule can now be SET to UTC and read straight off the list, which leaves a
  // third stamp of the same moment saying nothing the first two do not.

  // Only worked out when something is wrong, so the ordinary keystroke costs nothing.
  const positionErrors = useMemo(
    (): boolean[] | null =>
      parseError === null ? null : invalidPositions(expressionPositions, timeZoneId),
    [expressionPositions, parseError, timeZoneId]
  );

  const clockOptions = useMemo(
    () => [
      { value: '24', label: t(`${BASE_KEY}.repeat.clock24`) },
      { value: '12', label: t(`${BASE_KEY}.repeat.clock12`) }
    ],
    [t]
  );

  const zoneRegions = useMemo((): ZoneRegion[] => listZoneRegions(), []);

  // What the effect above puts in `timeZoneId` when the modal opens, worked out the same way so
  // the menu can offer it even when the browser's zone list has never heard of it.
  const openingZone = schedule?.timeZoneId || serverZone;

  /** Every zone the browser's own list offers, which is what tells a zone from a region row. */
  const offeredZones = useMemo((): Set<string> => {
    const zones = new Set<string>([serverZone, browserZone, UTC_ZONE]);
    for (const region of zoneRegions) {
      for (const zone of region.zones) zones.add(zone);
    }
    return zones;
  }, [browserZone, serverZone, zoneRegions]);

  /**
   * The server's zone, the browser's and UTC first, then one row per region holding its zones.
   * The offset is only worked out for the ones at the top and for whichever zone is selected:
   * reading it for all four hundred means four hundred zone-aware formatters built while the
   * modal is opening, and a city in a region the user picked deliberately does not need it.
   *
   * Rows spell a zone as its id, the same way the trigger above and the note below do. Trimming
   * it to a city put three spellings of one zone on screen for a single pick, and the region a
   * row sits under is already named by the submenu's own heading.
   */
  const zoneOptions = useMemo((): DropdownOption[] => {
    const options: DropdownOption[] = [];
    // All three are offered every time, even when two of them are the same zone: the three
    // answers to "which clock" are what the reader is choosing between, and dropping one because
    // this particular server happens to run on UTC hides the choice rather than simplifying it.
    // The row carries its own key so the same zone can appear twice, and the zone is read back
    // off the part after the colon exactly as a submenu pick is.
    const addZone = (key: string, zone: string, description?: string): void => {
      if (zone.length === 0) return;
      options.push({
        value: `${key}:${zone}`,
        label: zone,
        description,
        rightLabel: zoneOffsetLabel(zone, previewStart) ?? undefined
      });
    };
    // The zone the builder opened on can be one this browser's list does not carry: a schedule
    // written against a newer zone database or under a spelling since renamed, or a server
    // reporting a name this browser has not heard of. It keeps a row for as long as the modal is
    // open, so nothing in the menu is silently missing and looking at another zone is not a
    // one-way door out of the one actually saved.
    if (!offeredZones.has(openingZone)) addZone('saved', openingZone);
    addZone('server', serverZone, t(`${BASE_KEY}.timezone.serverOption`));
    addZone('browser', browserZone, t(`${BASE_KEY}.timezone.browserOption`));
    addZone('utc', UTC_ZONE, t(`${BASE_KEY}.timezone.utcOption`));
    for (const region of zoneRegions) {
      options.push({
        value: region.name,
        label: region.name,
        submenuTitle: region.name,
        submenu: region.zones.map((zone) => ({ value: zone, label: zone }))
      });
    }
    return options;
  }, [browserZone, offeredZones, openingZone, previewStart, serverZone, t, zoneRegions]);

  /**
   * The row that stands for the current zone. Rows carry a prefix so one zone can be offered
   * both as "the server's" and as a city under its region, which means the bare id matches no
   * row at all and nothing in a four-hundred-entry menu reads as chosen. The rows at the top win
   * over the region rows, because that is where a reader who picked one looks for it again.
   */
  const selectedZoneValue = useMemo((): string => {
    if (timeZoneId === serverZone) return `server:${timeZoneId}`;
    if (timeZoneId === browserZone) return `browser:${timeZoneId}`;
    if (timeZoneId === UTC_ZONE) return `utc:${timeZoneId}`;
    if (!offeredZones.has(timeZoneId)) return `saved:${timeZoneId}`;
    const slash = timeZoneId.indexOf('/');
    return slash < 0 ? timeZoneId : `${timeZoneId.slice(0, slash)}:${timeZoneId}`;
  }, [browserZone, offeredZones, serverZone, timeZoneId]);

  const handleZoneChange = useCallback(
    (value: string): void => {
      // A pick from a region's submenu arrives as "Europe:Europe/Berlin" and one from the top of
      // the menu as "server:America/Chicago". A click on the region row itself arrives as the
      // bare region name, which is not a zone and is ignored: the row exists to open the submenu,
      // not to be chosen. The zone the modal opened on passes even when the browser's list has no
      // entry for it, because the menu gives it a row for exactly that case.
      const separator = value.indexOf(':');
      const zone = separator < 0 ? value : value.slice(separator + 1);
      if (zone !== openingZone && !offeredZones.has(zone)) return;
      setTimeZoneId(zone);
    },
    [offeredZones, openingZone]
  );

  /**
   * Null when the browser's zone database has no entry for the id. Two zone-aware formatters go
   * into the answer, so it is worked out when the zone or the anchor changes rather than on every
   * keystroke in the form.
   */
  const zoneOffset = useMemo(
    (): string | null => zoneOffsetLabel(timeZoneId, previewStart),
    [previewStart, timeZoneId]
  );
  const zoneTriggerLabel = zoneOffset === null ? timeZoneId : `${timeZoneId} · ${zoneOffset}`;

  const repeatLabelId = `${fieldId}-repeat`;
  const windowLabelId = `${fieldId}-window`;
  const previewLabelId = `${fieldId}-preview`;
  const expressionId = `${fieldId}-expression`;

  const controlsDisabled = isDisabled || builderLocked;

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t(`${BASE_KEY}.title`)}
      size="lg"
      bodyFlexLayout
    >
      <div className="custom-schedule">
        {errorMessage && (
          <Alert color="red" className="custom-schedule-alert">
            {errorMessage}
          </Alert>
        )}

        <div className="custom-schedule-scroll" ref={scrollRef}>
          <div className="custom-schedule-body">
            {/* First, because every time below is read in this zone and on this face: choosing them
              afterwards means re-reading fields already filled in. Only one of the two belongs to
              the schedule - the zone is stored with it and decides when it fires, the face is a
              view setting for this modal alone. */}
            <section className="custom-schedule-section">
              <div className="custom-schedule-clock-row">
                <div className="custom-schedule-field custom-schedule-zone">
                  <span className="caps-label custom-schedule-field-label">
                    {t(`${BASE_KEY}.timezone.label`)}
                  </span>
                  <EnhancedDropdown
                    options={zoneOptions}
                    value={selectedZoneValue}
                    onChange={handleZoneChange}
                    customTriggerLabel={zoneTriggerLabel}
                    triggerAriaLabel={t(`${BASE_KEY}.timezone.label`)}
                    searchable
                    disabled={isDisabled}
                    variant="button"
                    dropdownWidth="w-72"
                    maxHeight="320px"
                  />
                </div>
                <div className="custom-schedule-field custom-schedule-clock-field">
                  <span className="caps-label custom-schedule-field-label">
                    {t(`${BASE_KEY}.repeat.clockLabel`)}
                  </span>
                  <div role="group" aria-label={t(`${BASE_KEY}.repeat.clockLabel`)}>
                    <SegmentedControl
                      options={clockOptions}
                      value={use24Hour ? '24' : '12'}
                      onChange={(value) => setUse24Hour(value === '24')}
                      size="md"
                      showLabels
                      fullWidth
                      className="custom-schedule-clock"
                    />
                  </div>
                </div>
              </div>
              <p className="custom-schedule-zone-note">{zoneNote}</p>
            </section>

            <section className="custom-schedule-section" aria-labelledby={repeatLabelId}>
              <span id={repeatLabelId} className="caps-label custom-schedule-legend">
                {t(`${BASE_KEY}.repeat.legend`)}
              </span>
              <SegmentedControl
                options={repeatOptions}
                value={draft.repeat}
                onChange={handleRepeatChange}
                size="sm"
                showLabels
                fullWidth
                className="custom-schedule-repeat"
              />

              <div key={draft.repeat} className="custom-schedule-fields">
                {draft.repeat === 'hourly' && (
                  <div className="custom-schedule-field">
                    <span className="caps-label custom-schedule-field-label">
                      {t(`${BASE_KEY}.repeat.everyNHours`)}
                    </span>
                    <NumberInput
                      value={draft.everyNHours}
                      min={1}
                      max={23}
                      disabled={controlsDisabled}
                      aria-label={t(`${BASE_KEY}.repeat.everyNHours`)}
                      onChange={(everyNHours) => updateDraft({ everyNHours })}
                    />
                  </div>
                )}

                {draft.repeat === 'weekly' && (
                  <div className="custom-schedule-field">
                    <span className="caps-label custom-schedule-field-label">
                      {t(`${BASE_KEY}.repeat.onDays`)}
                    </span>
                    <MultiSelectDropdown
                      className="custom-schedule-days"
                      options={weekdayOptions}
                      values={draft.weekdays}
                      onChange={handleWeekdaysChange}
                      disabled={controlsDisabled}
                      minSelections={0}
                      placeholder={t(`${BASE_KEY}.repeat.onDays`)}
                    />
                  </div>
                )}

                {draft.repeat === 'monthly' && (
                  <div className="custom-schedule-field">
                    <span className="caps-label custom-schedule-field-label">
                      {t(`${BASE_KEY}.repeat.onDayOfMonth`)}
                    </span>
                    <NumberInput
                      value={draft.dayOfMonth}
                      min={1}
                      max={31}
                      disabled={controlsDisabled}
                      aria-label={t(`${BASE_KEY}.repeat.onDayOfMonth`)}
                      onChange={(dayOfMonth) => updateDraft({ dayOfMonth })}
                    />
                  </div>
                )}

                <div className="custom-schedule-field">
                  <span className="caps-label custom-schedule-field-label">{timeFieldLabel}</span>
                  <TimeFields
                    label={timeFieldLabel}
                    time={draft.time}
                    disabled={controlsDisabled}
                    use24Hour={use24Hour}
                    minuteOnly={draft.repeat === 'hourly'}
                    onChange={(time) => updateDraft({ time })}
                  />
                </div>
              </div>
            </section>

            <section className="custom-schedule-section" aria-labelledby={windowLabelId}>
              <span id={windowLabelId} className="caps-label custom-schedule-legend">
                {t(`${BASE_KEY}.window.toggle`)}
              </span>
              <ToggleSwitch
                options={[
                  { value: 'false', label: t('common.off'), activeColor: 'default' },
                  { value: 'true', label: t('common.on'), activeColor: 'info' }
                ]}
                value={draft.windowEnabled ? 'true' : 'false'}
                onChange={(value) => updateDraft({ windowEnabled: value === 'true' })}
                disabled={isDisabled}
              />
              {/* The help sentence explains what happens to a run already going when the window
                shuts, which only matters once there is a window. Off by default, it is two
                sentences of rules about a feature the reader has not asked for yet. */}
              {draft.windowEnabled && (
                <>
                  <p className="custom-schedule-zone-note">{t(`${BASE_KEY}.window.help`)}</p>
                  <div className="custom-schedule-fields">
                    <div className="custom-schedule-field">
                      <span className="caps-label custom-schedule-field-label">
                        {t(`${BASE_KEY}.window.from`)}
                      </span>
                      <TimeFields
                        label={t(`${BASE_KEY}.window.from`)}
                        time={draft.windowStart}
                        disabled={isDisabled}
                        use24Hour={use24Hour}
                        onChange={(windowStart) => updateDraft({ windowStart })}
                      />
                    </div>
                    <div className="custom-schedule-field">
                      <span className="caps-label custom-schedule-field-label">
                        {t(`${BASE_KEY}.window.to`)}
                      </span>
                      <TimeFields
                        label={t(`${BASE_KEY}.window.to`)}
                        time={draft.windowEnd}
                        disabled={isDisabled}
                        use24Hour={use24Hour}
                        onChange={(windowEnd) => updateDraft({ windowEnd })}
                      />
                    </div>
                  </div>
                </>
              )}
            </section>

            <section className="custom-schedule-section" aria-labelledby={previewLabelId}>
              <span id={previewLabelId} className="caps-label custom-schedule-legend">
                {t(`${BASE_KEY}.preview.title`)}
              </span>
              {/* The panel rewrites itself on every control change, which a sighted user sees and a
                screen reader user would otherwise never be told about. Polite so it waits for a
                pause in typing rather than interrupting mid-field. */}
              <div className="well-surface custom-schedule-preview" aria-live="polite">
                {parseError !== null ? (
                  <p className="custom-schedule-message custom-schedule-message--error">
                    {t(`${BASE_KEY}.advanced.invalid`)}
                  </p>
                ) : builderLocked ? (
                  <p className="custom-schedule-message">
                    {t(`${BASE_KEY}.advanced.notRepresentable`)}
                  </p>
                ) : needsDay ? (
                  <p className="custom-schedule-message">{t(`${BASE_KEY}.preview.needsDay`)}</p>
                ) : (
                  <p className="custom-schedule-sentence">{sentence}</p>
                )}

                {/* The parser accepts a zone id it cannot resolve and only fails once it is asked
                  for a run, so nothing else on this panel can say what went wrong: the sentence
                  above still describes the schedule and the expression really is valid. */}
                {zoneOffset === null && (
                  <p className="custom-schedule-message custom-schedule-message--error">
                    {t(`${BASE_KEY}.timezone.unknown`)}
                  </p>
                )}

                {/* A zone the browser cannot resolve yields no runs either, and the sentence below
                  would then blame the window for a schedule that was never walked at all. */}
                {neverRuns && zoneOffset !== null && (
                  <p className="custom-schedule-message custom-schedule-message--error">
                    {windowIsZeroLength
                      ? t(`${BASE_KEY}.preview.windowZeroLength`)
                      : t(`${BASE_KEY}.preview.never`, {
                          time: formatClock(draft.time),
                          windowStart: formatClock(draft.windowStart),
                          windowEnd: formatClock(draft.windowEnd)
                        })}
                  </p>
                )}

                {nextRuns.length > 0 && (
                  <>
                    {/* The zone sits with the rows rather than only in the sentence above, which
                      an error, a hand-typed expression or a missing weekday each replace - the
                      list would then be five stamps with nothing saying which clock they are
                      on. */}
                    <div className="custom-schedule-runs-header">
                      <span className="caps-label caps-label--sm custom-schedule-runs-label">
                        {t(`${BASE_KEY}.preview.nextRuns`)}
                      </span>
                      <span className="custom-schedule-runs-zone">{timeZoneId}</span>
                    </div>
                    <ol className="custom-schedule-runs">
                      {nextRuns.map((run, index) => (
                        <li key={run.getTime()} className="custom-schedule-run">
                          <span className="tabular-nums custom-schedule-run-time">
                            {formatTimestamp(run, scheduleStampSettings)}
                          </span>
                          {index === 0 && (
                            <span className="custom-schedule-run-relative">
                              {formatTimeUntil(run, previewStart)}
                            </span>
                          )}
                        </li>
                      ))}
                    </ol>
                  </>
                )}
              </div>

              {/* The rows above are the schedule's own zone. This names the zone it is converting
                into, so the same moment on a second clock cannot be read as a second run. */}
              {nextRunHere !== null && (
                <p className="custom-schedule-zone-note">
                  {t(`${BASE_KEY}.preview.nextRunHere`, { time: nextRunHere, zone: displayZone })}
                </p>
              )}
            </section>

            <section className="custom-schedule-section">
              <Button
                type="button"
                variant="transparent"
                size="sm"
                className="custom-schedule-advanced-toggle"
                aria-expanded={advancedOpen}
                onClick={() => setAdvancedOpen((open) => !open)}
              >
                <ChevronRight
                  className={`custom-schedule-advanced-chevron${advancedOpen ? ' is-open' : ''}`}
                  size={14}
                />
                <span className="caps-label custom-schedule-advanced-label">
                  {t(`${BASE_KEY}.advanced.toggle`)}
                </span>
              </Button>

              <CollapsibleRegion open={advancedOpen}>
                <div className="custom-schedule-advanced" ref={advancedRef}>
                  <div className="custom-schedule-advanced-header">
                    <span className="caps-label custom-schedule-field-label">
                      {t(`${BASE_KEY}.advanced.expressionLabel`)}
                    </span>
                    {/* Off while the boxes already hold the default, so the button never offers to
                      do nothing, and so a hand-typed expression is the only thing it can lose. */}
                    <Button
                      variant="default"
                      size="sm"
                      onClick={handleResetExpression}
                      disabled={isDisabled || isDefaultExpression}
                    >
                      {t(`${BASE_KEY}.advanced.reset`)}
                    </Button>
                  </div>
                  {/* One box per cron position, each named. A single text field puts five
                    positions in one run of characters where "0 2 * * *" reads as "02***" and
                    nobody can tell which asterisk is the month. The boxes also teach the
                    format, which a modal that exists to hide cron owes the one person who
                    opens this drawer. */}
                  <div className="custom-schedule-cron">
                    {CRON_POSITIONS.map((position, index) => (
                      <div key={position} className="custom-schedule-cron-position">
                        <label
                          htmlFor={`${expressionId}-${position}`}
                          className="caps-label caps-label--sm custom-schedule-cron-label"
                        >
                          {t(`${BASE_KEY}.advanced.positions.${position}`)}
                        </label>
                        <input
                          {...noAutofill}
                          id={`${expressionId}-${position}`}
                          ref={(node) => {
                            positionInputs.current[index] = node;
                          }}
                          type="text"
                          value={expressionPositions[index]}
                          onChange={(event) => handlePositionChange(index, event)}
                          onKeyDown={(event) => handlePositionKeyDown(index, event)}
                          onPaste={(event) => handlePositionPaste(index, event)}
                          disabled={isDisabled}
                          spellCheck={false}
                          className={`themed-input control-h-md w-full px-2 tabular-nums custom-schedule-cron-input${
                            positionErrors?.[index] ? ' has-error' : ''
                          }`}
                        />
                      </div>
                    ))}
                  </div>
                  {/* No readout beside the boxes. It answered the same question as the panel above
                    and answered it worse: a clock time alone cannot show that "0 3 22 2 *" fires
                    on 22 February, and two places to look means one of them is the wrong one. */}
                  {parseError !== null && (
                    <p className="custom-schedule-message custom-schedule-message--error">
                      {parseError.length > 0 ? parseError : t(`${BASE_KEY}.advanced.invalid`)}
                    </p>
                  )}
                  {/* The stood-down-controls explanation is not repeated here. It already sits in
                    the panel above, which is the one place that stays visible when a saved
                    hand-written expression is loaded with this disclosure closed. */}
                </div>
              </CollapsibleRegion>
            </section>
          </div>
        </div>

        <div className="custom-schedule-footer">
          <Button variant="default" size="sm" onClick={onClose} disabled={isSaving}>
            {t(`${BASE_KEY}.cancel`)}
          </Button>
          <Button
            variant="filled"
            color="primary"
            size="sm"
            onClick={handleApply}
            loading={isSaving}
            disabled={!canSave}
          >
            {t(`${BASE_KEY}.save`)}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

export default CustomScheduleModal;
