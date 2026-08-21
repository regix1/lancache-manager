import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

interface ProgressBarProps {
  /** Current amount. A percentage bar passes 0-100 directly; a step counter passes the current step. */
  value: number;
  /** Ceiling `value` is measured against. Defaults to 100 for a percentage bar; a step counter passes its total. */
  max?: number;
  /** Track/fill thickness: `sm` = h-1 (the un-rounded step counter), `md` = h-1.5, `lg` = h-2.5. */
  height: 'sm' | 'md' | 'lg';
  /** Fill colour. `warning` is the one reset-in-progress bar; every other bar is `primary`. */
  color?: 'primary' | 'warning';
  /** Fill transition length in ms. The two tallest bars use 500, everything else uses 300. */
  duration?: 300 | 500;
  /** Rounded ends. Off only for the un-rounded step-counter bar. */
  rounded?: boolean;
  /** Already-translated `aria-label` for the `role="progressbar"` element. */
  label: string;
  /** Overrides the announced rounded percentage, for a bar whose value isn't a plain percentage (a step counter). */
  valueText?: string;
  /** Optional sibling caption, styled and translated by the caller. */
  caption?: ReactNode;
}

/**
 * Announced progress bar. Width is set through the `--progress-width` custom property
 * (the project's one accepted exception to the no-inline-styles rule) rather than an inline
 * `style={{ width }}` object.
 */
export const ProgressBar: React.FC<ProgressBarProps> = ({
  value,
  max = 100,
  height,
  color = 'primary',
  duration = 300,
  rounded = true,
  label,
  valueText,
  caption
}) => {
  const { t } = useTranslation();
  // Scale up before dividing: `(14.5 / 100) * 100` lands on 14.499999999999998 and announces 14
  // instead of 15, while `(14.5 * 100) / 100` is exact.
  const percent = (value * 100) / max;
  const heightClass = height === 'lg' ? 'h-2.5' : height === 'md' ? 'h-1.5' : 'h-1';
  const durationClass = duration === 500 ? 'duration-500' : 'duration-300';
  const colorClass = color === 'warning' ? 'bg-warning' : 'bg-primary';
  const roundedClass = rounded ? 'rounded-full' : '';

  return (
    <>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuenow={value}
        aria-valuemin={0}
        aria-valuemax={max}
        aria-valuetext={valueText ?? t('aria.progressValue', { percent: Math.round(percent) })}
        className={`w-full overflow-hidden bg-themed-tertiary ${heightClass} ${roundedClass}`}
      >
        <div
          className={`progress-bar-fill h-full transition-[width] ease-out ${durationClass} ${colorClass} ${roundedClass}`}
          style={{ '--progress-width': `${percent}%` } as React.CSSProperties}
        />
      </div>
      {caption}
    </>
  );
};
