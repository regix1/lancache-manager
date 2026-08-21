import { useCallback, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, ChevronUp } from 'lucide-react';

interface NumberInputProps {
  id?: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  disabled?: boolean;
  className?: string;
  'aria-label'?: string;
  onChange: (value: number) => void;
}

const clampValue = (value: number, min: number, max: number): number =>
  Math.min(max, Math.max(min, Math.trunc(value)));

export function NumberInput({
  id,
  value,
  min,
  max,
  step = 1,
  disabled = false,
  className = '',
  'aria-label': ariaLabel,
  onChange
}: NumberInputProps) {
  const { t } = useTranslation();
  const generatedId = useId();
  const inputId = id ?? generatedId;
  // What the box shows while it is being typed into, or null when it just shows `value`. An empty
  // box reads as 0 through `Number('')`, which clamps straight back to `min`, so without somewhere
  // to hold a half-typed entry a field sitting at its minimum can never be replaced: the digit you
  // type lands after the old one instead of over it.
  const [draft, setDraft] = useState<string | null>(null);

  const applyValue = useCallback(
    (nextValue: number) => {
      onChange(clampValue(nextValue, min, max));
    },
    [max, min, onChange]
  );

  const handleInputChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const raw = event.target.value;
    setDraft(raw);

    // Only a value already inside the range is published while typing. One that is empty, still
    // being typed, or past a bound waits for blur, so the parent never receives a number this
    // control's own bounds forbid.
    const parsed = Math.trunc(Number(raw));
    if (raw !== '' && Number.isFinite(parsed) && parsed >= min && parsed <= max) {
      onChange(parsed);
    }
  };

  // Typing stops here, so whatever is left resolves: a number past a bound becomes that bound, and
  // an empty or unreadable box falls back to the last good value rather than to `min`, so tabbing
  // away from a half-finished edit cannot quietly rewrite the setting.
  const handleInputBlur = () => {
    if (draft === null) {
      return;
    }
    const parsed = Number(draft);
    setDraft(null);
    if (draft !== '' && Number.isFinite(parsed)) {
      applyValue(parsed);
    }
  };

  const handleStep = (direction: 1 | -1) => {
    setDraft(null);
    applyValue(value + direction * step);
  };

  return (
    <div className={`number-input-wrapper ${className}`.trim()}>
      <input
        id={inputId}
        type="number"
        min={min}
        max={max}
        step={step}
        value={draft ?? value}
        disabled={disabled}
        aria-label={ariaLabel}
        className="themed-input w-full px-3 py-2"
        onChange={handleInputChange}
        onBlur={handleInputBlur}
      />
      <div className="spinner-buttons" aria-hidden={disabled}>
        <button
          type="button"
          className="spinner-btn"
          disabled={disabled || value >= max}
          onClick={() => handleStep(1)}
          tabIndex={-1}
          aria-label={t('ui.numberInput.increase')}
        >
          <ChevronUp />
        </button>
        <button
          type="button"
          className="spinner-btn"
          disabled={disabled || value <= min}
          onClick={() => handleStep(-1)}
          tabIndex={-1}
          aria-label={t('ui.numberInput.decrease')}
        >
          <ChevronDown />
        </button>
      </div>
    </div>
  );
}
