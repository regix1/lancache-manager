import React, { useEffect, useId, useRef, useState } from 'react';
import { SunMoon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { type Theme } from './types';

interface ThemeSliderProps {
  stops: Theme[];
  activeThemeId: string;
  // The theme actually in force, which is often not one of the stops: a community or custom theme
  // is applied from its own card and the slider still has to be able to put it back [16]
  activeTheme: Theme | null;
  disabled: boolean;
  onScrub: (theme: Theme) => void;
  // Resolves false when the choice could not be saved, so the control can stay where it was and
  // let the same stop be released again [17]
  onCommit: (themeId: string) => Promise<boolean>;
}

// The input holds a fractional position between the stops so the thumb follows the pointer instead
// of jumping. A thousandth of a stop is far under one pixel on the rendered track, so the travel
// shows no stepping. [1]
const VALUE_STEP = 0.001;

// Long enough to read as travel, short enough that a release still feels immediate.
const SETTLE_MS = 180;

const prefersReducedMotion = (): boolean =>
  typeof window !== 'undefined' &&
  typeof window.matchMedia === 'function' &&
  window.matchMedia('(prefers-reduced-motion: reduce)').matches;

export const ThemeSlider: React.FC<ThemeSliderProps> = ({
  stops,
  activeThemeId,
  activeTheme,
  disabled,
  onScrub,
  onCommit
}) => {
  const { t } = useTranslation();
  const headingId = useId();

  const activeIndex = stops.findIndex((entry: Theme) => entry.meta.id === activeThemeId);
  const maxIndex = stops.length - 1;

  // null until the control is moved: the thumb rests on the active theme, or at the darkest stop
  // when the active theme is not one of the built-ins
  const [position, setPosition] = useState<number | null>(null);

  const clampIndex = (candidate: number): number => Math.min(Math.max(candidate, 0), maxIndex);

  const restingPosition = activeIndex >= 0 ? activeIndex : 0;
  const shown = Math.min(Math.max(position ?? restingPosition, 0), maxIndex);
  const nearestIndex = clampIndex(Math.round(shown));
  const stop = stops[nearestIndex];
  // The stop lookup only ever finds a built-in, so a community theme left the rollback empty and
  // an abandoned scrub stayed on screen. The active theme itself is the snapshot to restore; the
  // stop is the fallback for the case where the parent cannot resolve it. [16]
  const committedTheme = activeTheme ?? (activeIndex >= 0 ? stops[activeIndex] : null);
  const progress = maxIndex > 0 ? (shown / maxIndex) * 100 : 0;

  // A community or custom theme leaves activeIndex at -1, and the control must not claim a
  // built-in it has not been asked for; once it has been moved, the scrub has genuinely applied
  // the stop under the thumb
  const hasSelection = activeIndex >= 0 || position !== null;

  // Repainting writes lancache_selected_theme, and loadSavedTheme prefers that key over the
  // server preference, so a move that never reaches a commit has to be undone or the next page
  // load boots into a theme nobody chose [12]
  const scrubRef = useRef(onScrub);
  const pendingRestore = useRef<Theme | null>(null);
  const committedId = useRef(activeThemeId);
  // Pointer release, key release and blur can all land within the same save. Holding the id in
  // flight keeps the later two from starting a second one, now that committedId no longer moves
  // until the save comes back [17]
  const committing = useRef<string | null>(null);
  const appliedIndex = useRef(activeIndex);
  const positionRef = useRef<number | null>(null);
  const settleFrame = useRef<number | null>(null);
  const settleTarget = useRef<number | null>(null);

  useEffect(() => {
    scrubRef.current = onScrub;
  }, [onScrub]);

  useEffect(() => {
    committedId.current = activeThemeId;
    appliedIndex.current = activeIndex;
    pendingRestore.current = null;
  }, [activeThemeId, activeIndex]);

  useEffect(
    () => () => {
      if (settleFrame.current !== null) cancelAnimationFrame(settleFrame.current);
      const restore = pendingRestore.current;
      if (restore) scrubRef.current(restore);
    },
    []
  );

  const currentPosition = (): number => positionRef.current ?? restingPosition;

  const moveTo = (next: number): void => {
    positionRef.current = next;
    setPosition(next);
  };

  const stopSettle = (): void => {
    if (settleFrame.current === null) return;
    cancelAnimationFrame(settleFrame.current);
    settleFrame.current = null;
  };

  // A release, and every arrow press, lands the thumb on a stop. Travelling there rather than
  // snapping keeps the motion continuous with the drag that preceded it; reduced motion gets the
  // landing without the travel.
  const settleTo = (target: number): void => {
    // A key press starts the travel and its key release lands here a frame later; restarting the
    // same journey would stutter it
    if (settleFrame.current !== null && settleTarget.current === target) return;
    stopSettle();
    settleTarget.current = target;
    const from = currentPosition();
    if (from === target || prefersReducedMotion()) {
      moveTo(target);
      return;
    }
    const startedAt = performance.now();
    const advance = (now: number): void => {
      const elapsed = (now - startedAt) / SETTLE_MS;
      if (elapsed >= 1) {
        settleFrame.current = null;
        moveTo(target);
        return;
      }
      moveTo(from + (target - from) * (1 - (1 - elapsed) ** 3));
      settleFrame.current = requestAnimationFrame(advance);
    };
    settleFrame.current = requestAnimationFrame(advance);
  };

  // applyTheme rewrites three localStorage keys and rebuilds the theme style element, so it runs
  // once per stop crossed and never once per pointer frame
  const applyStop = (nextIndex: number): void => {
    if (nextIndex === appliedIndex.current) return;
    appliedIndex.current = nextIndex;
    const next = stops[nextIndex];
    if (next.meta.id === committedId.current) {
      pendingRestore.current = null;
    } else if (pendingRestore.current === null && committedTheme) {
      pendingRestore.current = committedTheme;
    }
    onScrub(next);
  };

  const handleChange = (event: React.ChangeEvent<HTMLInputElement>): void => {
    const next = Number(event.target.value);
    if (!Number.isFinite(next)) return;
    stopSettle();
    const bounded = Math.min(Math.max(next, 0), maxIndex);
    moveTo(bounded);
    applyStop(clampIndex(Math.round(bounded)));
  };

  // The fine step exists for the pointer; left native, an arrow press would nudge the thumb by a
  // thousandth of a stop, so the keyboard moves stop to stop on its own
  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>): void => {
    if (disabled) return;
    const from = clampIndex(Math.round(currentPosition()));
    let target = from;
    if (event.key === 'ArrowRight' || event.key === 'ArrowUp' || event.key === 'PageUp') {
      target = from + 1;
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown' || event.key === 'PageDown') {
      target = from - 1;
    } else if (event.key === 'Home') {
      target = 0;
    } else if (event.key === 'End') {
      target = maxIndex;
    } else {
      return;
    }
    event.preventDefault();
    const next = clampIndex(target);
    applyStop(next);
    settleTo(next);
  };

  // Pointer release, key release and blur all land here; the committed-id check keeps the extra
  // events from writing the same choice twice
  const handleCommit = (): void => {
    if (positionRef.current === null) return;
    // The stop that is already on screen, not the thumb's live position: a key release arrives
    // while the thumb is still travelling, and rounding that would commit the stop it just left
    const chosen =
      appliedIndex.current >= 0 && appliedIndex.current <= maxIndex
        ? appliedIndex.current
        : clampIndex(Math.round(positionRef.current));
    applyStop(chosen);
    settleTo(chosen);
    const theme = stops[chosen];
    if (theme.meta.id === committedId.current || theme.meta.id === committing.current) return;
    committing.current = theme.meta.id;
    // A save that fails leaves the committed id and the rollback snapshot where they were, so
    // releasing this same stop again is a retry rather than a no-op, and walking away from the
    // page still puts the old theme back [17]
    const finishCommit = (saved: boolean): void => {
      committing.current = null;
      if (!saved) return;
      committedId.current = theme.meta.id;
      pendingRestore.current = null;
    };
    void onCommit(theme.meta.id).then(finishCommit, (): void => finishCommit(false));
  };

  const swatches = [
    stop.colors.primaryColor,
    stop.colors.secondaryColor,
    stop.colors.accentColor,
    stop.colors.bgPrimary,
    stop.colors.textPrimary
  ].filter((color): color is string => Boolean(color));

  return (
    <div className="theme-slider well-surface" role="group" aria-labelledby={headingId}>
      <div className="theme-slider-head">
        <SunMoon className="w-4 h-4 text-themed-muted flex-shrink-0" />
        <span id={headingId} className="caps-label">
          {t('management.themes.slider.title')}
        </span>
        {/* An em dash rather than a theme name: naming one here would say it is applied when it
            is not */}
        <span
          className={
            hasSelection ? 'theme-slider-value' : 'theme-slider-value theme-slider-value-none'
          }
        >
          {hasSelection ? stop.meta.name : '—'}
        </span>
      </div>

      <div className="theme-slider-swatches">
        {swatches.map((color: string, idx: number) => (
          <span
            key={idx}
            className="theme-slider-swatch"
            style={{ '--swatch-color': color } as React.CSSProperties}
          />
        ))}
      </div>

      {/* aria-valuenow is the stop the thumb has reached, not the fraction the input holds: a
          fractional value would be read out as a number that means nothing */}
      {/* focus-ring names where the keyboard outline comes from; the stylesheet only gives the
          control the radius the outline follows */}
      <input
        type="range"
        className="theme-slider-input focus-ring"
        min={0}
        max={maxIndex}
        step={VALUE_STEP}
        value={shown}
        disabled={disabled}
        aria-label={t('management.themes.slider.label')}
        aria-valuenow={nearestIndex}
        aria-valuemin={0}
        aria-valuemax={maxIndex}
        aria-valuetext={stop.meta.name}
        style={{ '--range-progress': `${progress}%` } as React.CSSProperties}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
        onPointerDown={stopSettle}
        onPointerUp={handleCommit}
        onKeyUp={handleCommit}
        onBlur={handleCommit}
      />

      <div className="theme-slider-stops">
        {/* Marked off the slider's own position, not the committed id: committing is async, so
            keying this to the saved theme would leave the bold label behind the thumb */}
        {stops.map((entry: Theme, idx: number) => (
          <span
            key={entry.meta.id}
            className={
              hasSelection && idx === nearestIndex
                ? 'theme-slider-stop theme-slider-stop-current'
                : 'theme-slider-stop'
            }
          >
            {entry.meta.name}
          </span>
        ))}
      </div>

      <p className="theme-slider-hint">
        {(hasSelection && stop.meta.description) || t('management.themes.slider.hint')}
      </p>
    </div>
  );
};
