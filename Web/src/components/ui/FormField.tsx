import { useId } from 'react';
import type { FormFieldProps } from './FormField.types';

/**
 * Label, hint and error line for one form control, with the accessibility wiring
 * between them.
 *
 * The control arrives as a function child rather than a plain one because the id
 * has to reach it: the label's `htmlFor`, the control's `id` and the
 * `aria-describedby` pointing back at the hint and error all have to agree, and a
 * plain child cannot be given a generated id without cloning it. A function child
 * also keeps the shape open - the same wrapper holds an `<input>`, a password box
 * with a reveal button beside it, a `<textarea>` or a button that opens a picker.
 *
 * It deliberately does not own the control, so padding, height and the rest of the
 * input recipe stay with the caller.
 *
 * Renders no wrapper element: callers already sit inside a layout div, and adding
 * a second one would change spacing at every site.
 */
export default function FormField({ label, error, hint, required, children }: FormFieldProps) {
  const id = useId();
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;
  const describedBy = [error ? errorId : null, hint ? hintId : null].filter(Boolean).join(' ');

  return (
    <>
      <label htmlFor={id} className="form-field-label">
        {label}
        {required && (
          <span className="text-themed-error" aria-hidden="true">
            {' '}
            *
          </span>
        )}
      </label>
      {children({
        id,
        'aria-invalid': error ? true : undefined,
        'aria-describedby': describedBy || undefined,
        'aria-required': required || undefined
      })}
      {/* Announced on appearance: a validation failure that only shows visually never
          reaches someone who is not looking at the field. */}
      {error && (
        <p id={errorId} role="alert" className="text-xs text-themed-error mt-1">
          {error}
        </p>
      )}
      {hint && (
        <p id={hintId} className="text-xs text-themed-muted mt-1">
          {hint}
        </p>
      )}
    </>
  );
}
