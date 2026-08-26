import type { ChangeEvent } from 'react';
import FormField from '@components/ui/FormField';
import { PasswordField } from '@components/ui/PasswordField';
import type {
  PostgresConnectionField,
  PostgresConnectionLabels,
  PostgresConnectionValues
} from './PostgresConnectionFields.types';

interface PostgresConnectionFieldsProps {
  values: PostgresConnectionValues;
  labels: PostgresConnectionLabels;
  onFieldChange: (field: PostgresConnectionField, value: string) => void;
  /** Full input class string, copied verbatim from the caller's family of forms. */
  inputClassName: string;
  disabled?: boolean;
  /** Renders the password field as a `PasswordField` with a reveal toggle when set. Only
   *  `ExternalDatabaseSetupStep` offers a reveal today; `DatabaseImportForm` does not. */
  passwordReveal?: { showLabel: string; hideLabel: string };
  /** Per-field validation message, shown under the matching input. `ExternalDatabaseSetupStep`
   *  validates before submit; `DatabaseImportForm` has no client-side validation and omits this. */
  errors?: Partial<Record<PostgresConnectionField, string | null>>;
}

/**
 * Host / port / database / username / password fields for a Postgres connection, shared by
 * the external-database setup step and the historical-data import form. The port field uses
 * `inputMode="numeric"` on a text input rather than `type="number"`, so it never gets the
 * browser's native spinner.
 */
export const PostgresConnectionFields: React.FC<PostgresConnectionFieldsProps> = ({
  values,
  labels,
  onFieldChange,
  inputClassName,
  disabled,
  passwordReveal,
  errors
}) => {
  const handleChange =
    (field: PostgresConnectionField) => (event: ChangeEvent<HTMLInputElement>) => {
      onFieldChange(field, event.target.value);
    };

  return (
    <>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div className="sm:col-span-2">
          <FormField label={labels.host} error={errors?.host}>
            {(field) => (
              <input
                {...field}
                type="text"
                value={values.host}
                onChange={handleChange('host')}
                disabled={disabled}
                className={inputClassName}
              />
            )}
          </FormField>
        </div>
        <div>
          <FormField label={labels.port} error={errors?.port}>
            {(field) => (
              <input
                {...field}
                type="text"
                inputMode="numeric"
                value={values.port}
                onChange={handleChange('port')}
                disabled={disabled}
                className={inputClassName}
              />
            )}
          </FormField>
        </div>
      </div>

      <FormField label={labels.database} error={errors?.database}>
        {(field) => (
          <input
            {...field}
            type="text"
            value={values.database}
            onChange={handleChange('database')}
            disabled={disabled}
            className={inputClassName}
          />
        )}
      </FormField>

      <FormField label={labels.username} error={errors?.username}>
        {(field) => (
          <input
            {...field}
            type="text"
            value={values.username}
            onChange={handleChange('username')}
            disabled={disabled}
            className={inputClassName}
          />
        )}
      </FormField>

      {passwordReveal ? (
        <PasswordField
          label={labels.password}
          value={values.password}
          onChange={handleChange('password')}
          error={errors?.password}
          disabled={disabled}
          inputClassName={inputClassName}
          showPasswordLabel={passwordReveal.showLabel}
          hidePasswordLabel={passwordReveal.hideLabel}
        />
      ) : (
        <FormField label={labels.password} error={errors?.password}>
          {(field) => (
            <input
              {...field}
              type="password"
              // A database password typed into a server's own setup form, not a sign-in this
              // browser should remember and offer back later.
              autoComplete="new-password"
              value={values.password}
              onChange={handleChange('password')}
              disabled={disabled}
              className={inputClassName}
            />
          )}
        </FormField>
      )}
    </>
  );
};
