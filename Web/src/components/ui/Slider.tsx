import React from 'react';

interface SliderProps {
  value: number;
  min: number;
  max: number;
  step?: number;
  disabled?: boolean;
  className?: string;
  // Required: a range input with no name is announced only as "slider"
  'aria-label': string;
  // For a control whose value is a position rather than the number it stands for. Left off, the
  // browser reports the input's own value.
  'aria-valuenow'?: number;
  'aria-valuetext'?: string;
  onChange: (value: number) => void;
  onKeyDown?: (event: React.KeyboardEvent<HTMLInputElement>) => void;
  onKeyUp?: (event: React.KeyboardEvent<HTMLInputElement>) => void;
  onPointerDown?: (event: React.PointerEvent<HTMLInputElement>) => void;
  onPointerUp?: (event: React.PointerEvent<HTMLInputElement>) => void;
  onBlur?: (event: React.FocusEvent<HTMLInputElement>) => void;
}

// The filled part of the track is a gradient stop in forms.css that reads --range-progress. The
// browser gives no selector for "left of the thumb", so the percentage has to be written onto the
// element from here; a range input left without it paints an empty track at every value.
const trackProgress = (value: number, min: number, max: number): number => {
  const span = max - min;
  if (span <= 0) return 0;
  return Math.min(Math.max(((value - min) / span) * 100, 0), 100);
};

export function Slider({
  value,
  min,
  max,
  step = 1,
  disabled = false,
  className = '',
  'aria-label': ariaLabel,
  'aria-valuenow': ariaValueNow,
  'aria-valuetext': ariaValueText,
  onChange,
  onKeyDown,
  onKeyUp,
  onPointerDown,
  onPointerUp,
  onBlur
}: SliderProps) {
  const progress = trackProgress(value, min, max);

  const handleChange = (event: React.ChangeEvent<HTMLInputElement>): void => {
    const next = Number(event.target.value);
    if (!Number.isFinite(next)) return;
    onChange(next);
  };

  return (
    <input
      type="range"
      className={className}
      min={min}
      max={max}
      step={step}
      value={value}
      disabled={disabled}
      aria-label={ariaLabel}
      aria-valuenow={ariaValueNow}
      aria-valuemin={min}
      aria-valuemax={max}
      aria-valuetext={ariaValueText}
      style={{ '--range-progress': `${progress}%` } as React.CSSProperties}
      onChange={handleChange}
      onKeyDown={onKeyDown}
      onKeyUp={onKeyUp}
      onPointerDown={onPointerDown}
      onPointerUp={onPointerUp}
      onBlur={onBlur}
    />
  );
}
