import React, { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronRight } from 'lucide-react';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { Modal } from '@components/ui/Modal';
import { MultiSelectDropdown, type MultiSelectOption } from '@components/ui/MultiSelectDropdown';
import { NumberInput } from '@components/ui/NumberInput';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { useTimezone } from '@contexts/useTimezone';
import { formatTimestamp, type TimestampSettings } from '@utils/dateTimeFormat';
import { getEffectiveTimezone, getServerTimezone } from '@utils/timezone';
import {
  CRON_POSITIONS,
  DEFAULT_SCHEDULE_DRAFT,
  buildExpression,
  clockOfRun,
  computeNextRuns,
  describeSchedule,
  expressionError,
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
  toTwentyFourHour,
  zoneOffsetHours
} from './customSchedulePreview';
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
  const { useLocalTimezone, use24HourFormat } = useTimezone();
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
  // A view setting for this modal only, seeded from the global clock preference. Writing back
  // to the global one would change how every timestamp in the app renders, which is not what
  // someone switching the face on a time field is asking for.
  const [use24Hour, setUse24Hour] = useState(use24HourFormat);
  // Anchoring the preview to the moment the modal opened keeps the five rows still while the
  // user works, instead of every keystroke re-reading the clock and shifting the list.
  const [previewFrom, setPreviewFrom] = useState<number>(() => Date.now());

  // Read through a ref so a caller passing a fresh object literal each render cannot restart
  // the form underneath the user.
  const scheduleRef = useRef<CustomSchedule | null>(schedule);
  scheduleRef.current = schedule;

  // Read through a ref for the same reason: the global preference must seed the local face on
  // open without re-running this effect and resetting the form when it changes elsewhere.
  const clockPreferenceRef = useRef(use24HourFormat);
  clockPreferenceRef.current = use24HourFormat;

  useEffect(() => {
    if (!opened) return;
    const saved = scheduleRef.current;
    const restored = saved ? readSchedule(saved) : null;
    setDraft(restored ?? DEFAULT_SCHEDULE_DRAFT);
    setManualExpression(saved && !restored ? saved.expression : null);
    setTypedPositions(null);
    setAdvancedOpen(false);
    setUse24Hour(clockPreferenceRef.current);
    setPreviewFrom(Date.now());
  }, [opened]);

  const timeZoneId = getServerTimezone();
  const browserZone = getEffectiveTimezone(true);
  const previewStart = useMemo(() => new Date(previewFrom), [previewFrom]);

  const formatClock = useCallback(
    (time: ClockTime): string => {
      const carrier = new Date(CLOCK_CARRIER_DAY);
      carrier.setHours(time.hour, time.minute, 0, 0);
      return formatTimestamp(carrier, {
        // The carrier is built in browser-local time and reads as a wall clock, so it must
        // not be shifted into another zone on the way out.
        useLocalTimezone: true,
        use24Hour,
        forceYear: false,
        style: 'timeOnly'
      });
    },
    [use24Hour]
  );

  const expression = manualExpression ?? buildExpression(draft);
  const derivedPositions = useMemo((): string[] => splitExpression(expression), [expression]);
  const expressionPositions = typedPositions ?? derivedPositions;
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

  // What the expression alone fires at, with the window deliberately left out. The readout
  // beside the field describes the field; the window's effect is the panel above's job, and
  // folding it in here would make the two disagree whenever a window excludes the next run.
  const expressionRuns = useMemo(
    (): Date[] =>
      parseError !== null
        ? []
        : computeNextRuns(
            { expression, timeZoneId, windowStart: null, windowEnd: null },
            1,
            previewStart
          ),
    [expression, parseError, previewStart, timeZoneId]
  );

  const neverRuns = !needsDay && parseError === null && nextRuns.length === 0;
  const hasChanges = !isSameSchedule(candidate, schedule);
  const canSave =
    !isDisabled && !isSaving && !needsDay && parseError === null && !neverRuns && hasChanges;

  const stampSettings: TimestampSettings = {
    useLocalTimezone,
    // The whole modal speaks one clock, so the preview rows follow the modal's own face
    // rather than the global preference the face was seeded from.
    use24Hour,
    forceYear: false
  };

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

  const handlePositionChange = useCallback(
    (index: number, value: string): void => {
      // A cron position is never longer than a short list or step, and a space here would
      // silently turn one position into two.
      const cleaned = value.replace(/\s+/g, '');
      applyPositions(expressionPositions.map((current, at) => (at === index ? cleaned : current)));
    },
    [applyPositions, expressionPositions]
  );

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
  const zoneNote = t(`${BASE_KEY}.timezone.server`, { zone: timeZoneId });
  const sentence = describeSchedule(draft, timeZoneId, t, formatClock);
  const cronReadout =
    expressionRuns.length > 0
      ? formatClock(clockOfRun(expressionRuns[0], timeZoneId))
      : t(`${BASE_KEY}.advanced.readoutUnknown`);

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

  const repeatLabelId = `${fieldId}-repeat`;
  const windowLabelId = `${fieldId}-window`;
  const previewLabelId = `${fieldId}-preview`;
  const expressionId = `${fieldId}-expression`;

  const controlsDisabled = isDisabled || builderLocked;

  return (
    <Modal opened={opened} onClose={onClose} title={t(`${BASE_KEY}.title`)} size="lg">
      <div className="custom-schedule">
        {errorMessage && (
          <Alert color="red" className="custom-schedule-alert">
            {errorMessage}
          </Alert>
        )}

        <div className="custom-schedule-body">
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

            {/* The clock face sits with the timezone line because both say how to read the
                time above them, and one control for the whole modal keeps every field on the
                same face. It is a view setting, so it stays quieter than the fields. */}
            <div className="custom-schedule-clock-row">
              <p className="custom-schedule-zone-note">{zoneNote}</p>
              <div role="group" aria-label={t(`${BASE_KEY}.repeat.clockLabel`)}>
                <SegmentedControl
                  options={clockOptions}
                  value={use24Hour ? '24' : '12'}
                  onChange={(value) => setUse24Hour(value === '24')}
                  size="sm"
                  showLabels
                  className="custom-schedule-clock"
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
              title={t(`${BASE_KEY}.window.toggle`)}
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

              {neverRuns && (
                <p className="custom-schedule-message custom-schedule-message--error">
                  {t(`${BASE_KEY}.preview.never`, {
                    time: formatClock(draft.time),
                    windowStart: formatClock(draft.windowStart),
                    windowEnd: formatClock(draft.windowEnd)
                  })}
                </p>
              )}

              {nextRuns.length > 0 && (
                <>
                  <span className="caps-label caps-label--sm custom-schedule-runs-label">
                    {t(`${BASE_KEY}.preview.nextRuns`)}
                  </span>
                  <ol className="custom-schedule-runs">
                    {nextRuns.map((run, index) => (
                      <li key={run.getTime()} className="custom-schedule-run">
                        <span className="tabular-nums custom-schedule-run-time">
                          {formatTimestamp(run, stampSettings)}
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

            {browserZone !== timeZoneId && (
              <p className="custom-schedule-zone-note">
                {t(`${BASE_KEY}.timezone.browserDiffers`, {
                  zone: browserZone,
                  offset: zoneOffsetHours(browserZone, timeZoneId, previewStart)
                })}
              </p>
            )}
          </section>

          <section className="custom-schedule-section">
            <button
              type="button"
              className="custom-schedule-advanced-toggle focus-ring"
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
            </button>

            <CollapsibleRegion open={advancedOpen}>
              <div className="custom-schedule-advanced">
                <span className="caps-label custom-schedule-field-label">
                  {t(`${BASE_KEY}.advanced.expressionLabel`)}
                </span>
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
                        id={`${expressionId}-${position}`}
                        type="text"
                        value={expressionPositions[index]}
                        onChange={(event) => handlePositionChange(index, event.target.value)}
                        onPaste={(event) => handlePositionPaste(index, event)}
                        disabled={isDisabled}
                        spellCheck={false}
                        autoComplete="off"
                        className={`themed-input control-h-md focus-ring--inset w-full px-2 tabular-nums custom-schedule-cron-input${
                          positionErrors?.[index] ? ' has-error' : ''
                        }`}
                      />
                    </div>
                  ))}
                  <div className="custom-schedule-cron-readout">
                    <span className="caps-label caps-label--sm custom-schedule-cron-label">
                      {t(`${BASE_KEY}.advanced.readoutLabel`)}
                    </span>
                    <span className="tabular-nums custom-schedule-cron-time">{cronReadout}</span>
                  </div>
                </div>
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

        <div className="custom-schedule-footer">
          <Button variant="default" size="sm" onClick={onClose} disabled={isSaving}>
            {t(`${BASE_KEY}.cancel`)}
          </Button>
          <Button
            variant="filled"
            color="green"
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
