import { useState, type ChangeEvent } from 'react';
import { Eye, EyeOff } from 'lucide-react';
import FormField from '@components/ui/FormField';

interface PasswordFieldProps {
  label: string;
  value: string;
  onChange: (event: ChangeEvent<HTMLInputElement>) => void;
  error?: string | null;
  placeholder?: string;
  autoComplete?: string;
  disabled?: boolean;
  /** Input class string from the caller's family of steps; the reveal gutter is added here. */
  inputClassName: string;
  /** `aria-label` for the reveal button while the password is hidden. */
  showPasswordLabel: string;
  /** `aria-label` for the reveal button while the password is visible. */
  hidePasswordLabel: string;
}

/**
 * Password input with a show/hide toggle, wrapping the shared FormField. The toggle button
 * is keyboard reachable and carries a visible focus ring and an accessible name; the five
 * markup copies this replaces removed it from the tab order and had neither.
 */
export const PasswordField: React.FC<PasswordFieldProps> = ({
  label,
  value,
  onChange,
  error = null,
  placeholder,
  autoComplete,
  disabled,
  inputClassName,
  showPasswordLabel,
  hidePasswordLabel
}) => {
  const [visible, setVisible] = useState(false);

  return (
    <FormField label={label} error={error}>
      {(field) => (
        <div className="relative">
          <input
            {...field}
            type={visible ? 'text' : 'password'}
            value={value}
            onChange={onChange}
            placeholder={placeholder}
            autoComplete={autoComplete}
            disabled={disabled}
            // The reveal button sits over the input's right edge, so the room for it
            // belongs to the component that renders it rather than to every caller.
            className={`${inputClassName} pr-10`}
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded text-themed-muted focus-ring"
            onClick={() => setVisible((prev) => !prev)}
            aria-label={visible ? hidePasswordLabel : showPasswordLabel}
          >
            {visible ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
          </button>
        </div>
      )}
    </FormField>
  );
};
