import { useCallback, useId } from 'react';
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
  const generatedId = useId();
  const inputId = id ?? generatedId;

  const applyValue = useCallback(
    (nextValue: number) => {
      onChange(clampValue(nextValue, min, max));
    },
    [max, min, onChange]
  );

  const handleInputChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const parsed = Number(event.target.value);
    if (Number.isFinite(parsed)) {
      applyValue(parsed);
    }
  };

  const handleStep = (direction: 1 | -1) => {
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
        value={value}
        disabled={disabled}
        aria-label={ariaLabel}
        className="themed-input w-full px-3 py-2"
        onChange={handleInputChange}
      />
      <div className="spinner-buttons" aria-hidden={disabled}>
        <button
          type="button"
          className="spinner-btn"
          disabled={disabled || value >= max}
          onClick={() => handleStep(1)}
          tabIndex={-1}
          aria-label="Increase value"
        >
          <ChevronUp />
        </button>
        <button
          type="button"
          className="spinner-btn"
          disabled={disabled || value <= min}
          onClick={() => handleStep(-1)}
          tabIndex={-1}
          aria-label="Decrease value"
        >
          <ChevronDown />
        </button>
      </div>
    </div>
  );
}
