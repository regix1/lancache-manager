import type { ReactNode } from 'react';

/**
 * The wiring FormField hands to the control it wraps. Spread it onto the input,
 * textarea or button so the label, the hint and the error all point at the same
 * element. Every value is `undefined` when it does not apply, which React renders
 * as the attribute being absent rather than present-and-false.
 */
export interface FormFieldControl {
  id: string;
  'aria-invalid': true | undefined;
  'aria-describedby': string | undefined;
  'aria-required': true | undefined;
}

export interface FormFieldProps {
  /** Caption above the control. Already translated by the caller. */
  label: string;
  /**
   * Validation failure. Its presence is what flips `aria-invalid` on the control.
   * Accepts null because the forms that carry per-field errors hold them as
   * `string | null`.
   */
  error?: string | null;
  /** Explanatory line under the control. */
  hint?: string;
  /** Adds the asterisk and `aria-required`. */
  required?: boolean;
  children: (field: FormFieldControl) => ReactNode;
}
