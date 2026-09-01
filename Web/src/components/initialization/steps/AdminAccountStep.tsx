import React, { useState, useCallback } from 'react';
import { UserPlus, CheckCircle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import { PasswordField } from '@components/ui/PasswordField';
import { PasswordStrengthMeter } from '@components/ui/PasswordStrengthMeter';
import { StepHeader } from '@components/initialization/StepHeader';
import { useSetupStatus } from '@contexts/useSetupStatus';
import ApiService from '@services/api.service';
import { API_BASE } from '@utils/constants';
import { getErrorMessage } from '@utils/error';

interface FormState {
  username: string;
  password: string;
  confirmPassword: string;
  apiKey: string;
}

interface FormErrors {
  username: string | null;
  password: string | null;
  confirmPassword: string | null;
  apiKey: string | null;
}

interface ValidationFieldError {
  field: string;
  message: string;
}

/**
 * Every shape POST /api/account-setup/first-admin can answer with. `success` comes from
 * MessageResponse on the way through, `error` from AccountSetupRefusalResponse and ErrorResponse,
 * and `errors` only from the password rules failing in ValidationFilter, whose own `error` is the
 * unhelpful "Validation failed". `stageKey` rides beside `error` on every refusal the endpoint
 * decides itself, naming the reason as an i18n key so it can be read in the operator's language.
 */
interface CreateAccountResponse {
  success?: boolean;
  error?: string;
  stageKey?: string;
  errors?: ValidationFieldError[];
}

export const AdminAccountStep: React.FC = () => {
  const { t } = useTranslation();
  const { setupStatus, refreshSetupStatus } = useSetupStatus();
  const recovering = setupStatus?.mainAdminRecoveryAvailable === true;
  const [form, setForm] = useState<FormState>({
    username: '',
    password: '',
    confirmPassword: '',
    apiKey: ''
  });
  const [errors, setErrors] = useState<FormErrors>({
    username: null,
    password: null,
    confirmPassword: null,
    apiKey: null
  });
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [accountCreated, setAccountCreated] = useState(false);

  const validateForm = useCallback((): boolean => {
    const newErrors: FormErrors = {
      username: null,
      password: null,
      confirmPassword: null,
      apiKey: null
    };

    const username = form.username.trim();

    if (!username) {
      newErrors.username = t('initialization.adminAccount.errors.usernameRequired');
    } else if (username.length > 64) {
      newErrors.username = t('initialization.adminAccount.errors.usernameTooLong');
    }

    // Same rules and same order as AccountCredentialsRequestValidator, so the user is told before
    // submitting rather than by a 400. The server stays the authority.
    if (!form.password) {
      newErrors.password = t('initialization.adminAccount.errors.passwordRequired');
    } else if (form.password.length < 12) {
      newErrors.password = t('initialization.adminAccount.errors.passwordTooShort');
    } else if (form.password.length > 256) {
      newErrors.password = t('initialization.adminAccount.errors.passwordTooLong');
    } else {
      // char.IsLower and its siblings are Unicode-aware on the server, so ASCII-only classes here
      // would reject a password the server accepts and leave the operator unable to create the one
      // account there is.
      const characterClasses =
        (/\p{Ll}/u.test(form.password) ? 1 : 0) +
        (/\p{Lu}/u.test(form.password) ? 1 : 0) +
        (/\p{Nd}/u.test(form.password) ? 1 : 0) +
        (/[^\p{L}\p{Nd}]/u.test(form.password) ? 1 : 0);

      if (characterClasses < 3) {
        newErrors.password = t('initialization.adminAccount.errors.passwordCharacterClasses');
      } else if (form.password.toLowerCase() === username.toLowerCase()) {
        newErrors.password = t('initialization.adminAccount.errors.passwordSameAsUsername');
      }
    }

    if (!form.confirmPassword) {
      newErrors.confirmPassword = t('initialization.adminAccount.errors.confirmPasswordRequired');
    } else if (form.password !== form.confirmPassword) {
      newErrors.confirmPassword = t('initialization.adminAccount.errors.passwordsDoNotMatch');
    }

    if (!form.apiKey.trim()) {
      newErrors.apiKey = t('initialization.adminAccount.errors.apiKeyRequired');
    }

    setErrors(newErrors);
    return Object.values(newErrors).every((error) => error === null);
  }, [form, t]);

  const handleInputChange = useCallback(
    (field: keyof FormState) => (e: React.ChangeEvent<HTMLInputElement>) => {
      setForm((prev: FormState) => ({ ...prev, [field]: e.target.value }));
      setErrors((prev: FormErrors) => ({ ...prev, [field]: null }));
      setSubmitError(null);
    },
    []
  );

  const handleSubmit = useCallback(async () => {
    if (!validateForm()) {
      return;
    }

    setIsSubmitting(true);
    setSubmitError(null);

    try {
      const response = await fetch(
        `${API_BASE}/account-setup/${recovering ? 'recover-main-admin' : 'first-admin'}`,
        ApiService.getJsonFetchOptions(
          {
            username: form.username.trim(),
            password: form.password,
            apiKey: form.apiKey.trim()
          },
          { method: 'POST' }
        )
      );

      // The endpoint permits five attempts a minute per address, and a throttled request carries
      // no body at all, so parsing it as JSON would report a syntax error instead of the wait.
      if (response.status === 429) {
        setSubmitError(t('initialization.adminAccount.errors.tooManyAttempts'));
        return;
      }

      const data: CreateAccountResponse = await response.json();

      if (response.ok && data.success) {
        setAccountCreated(true);
        // The account now exists and the server has closed the claim window, so re-reading setup
        // status drops the recovery gate and lets this screen close.
        setTimeout(() => {
          void refreshSetupStatus();
        }, 1500);
      } else {
        // The endpoint's own refusals travel as an i18n key beside the English sentence, so the
        // reason is read in the operator's language. The sentence is what a key this build has no
        // translation for falls back to. A broken password rule is the exception: it comes back as
        // a per-field message under a key that only ever says "Validation failed", so the field
        // message wins and names the rule that was actually broken.
        const sentence =
          data.errors?.[0]?.message ||
          data.error ||
          t(
            recovering
              ? 'initialization.adminAccount.errors.recoverFailed'
              : 'initialization.adminAccount.errors.createFailed'
          );
        setSubmitError(
          data.errors?.[0]?.message ||
            (data.stageKey ? t(data.stageKey, { defaultValue: sentence }) : sentence)
        );
      }
    } catch (error: unknown) {
      setSubmitError(getErrorMessage(error) || t('initialization.adminAccount.errors.network'));
    } finally {
      setIsSubmitting(false);
    }
  }, [form, recovering, refreshSetupStatus, t, validateForm]);

  if (accountCreated) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<CheckCircle className="w-7 h-7 icon-success" />}
          iconBackground="bg-themed-success"
          title={t(
            recovering
              ? 'initialization.adminAccount.recoveredHeading'
              : 'initialization.adminAccount.createdHeading'
          )}
          description={t(
            recovering
              ? 'initialization.adminAccount.recoveredDescription'
              : 'initialization.adminAccount.createdDescription'
          )}
        />
      </div>
    );
  }

  return (
    <form
      className="space-y-5"
      onSubmit={(event) => {
        event.preventDefault();
        void handleSubmit();
      }}
    >
      <StepHeader
        icon={<UserPlus className="w-7 h-7 icon-info" />}
        iconBackground="bg-themed-info"
        title={t(
          recovering
            ? 'initialization.adminAccount.recoveryHeading'
            : 'initialization.adminAccount.heading'
        )}
        description={t(
          recovering
            ? 'initialization.adminAccount.recoveryDescription'
            : 'initialization.adminAccount.description'
        )}
      />

      {/* The session cookie is only marked Secure on an HTTPS request, because forcing it on a
          plain-HTTP LAN deployment would stop the browser sending it at all. That was a fair trade
          while the only credential was an API key typed once; a password typed on every sign-in
          crosses the same network. isSecureContext is the browser's own answer, so a loopback
          address during setup is not warned about. */}
      {!window.isSecureContext && (
        <Alert color="warning" title={t('initialization.adminAccount.insecureConnection.title')}>
          {t('initialization.adminAccount.insecureConnection.description')}
        </Alert>
      )}

      {/* Username Input */}
      <div>
        <FormField label={t('initialization.adminAccount.usernameLabel')} error={errors.username}>
          {(field) => (
            <input
              {...field}
              type="text"
              value={form.username}
              onChange={handleInputChange('username')}
              placeholder={t('initialization.adminAccount.usernamePlaceholder')}
              className="w-full px-3 py-2.5 themed-input"
              autoComplete="username"
              disabled={isSubmitting}
            />
          )}
        </FormField>
      </div>

      {/* Password Input */}
      <div>
        <PasswordField
          label={t('initialization.adminAccount.passwordLabel')}
          value={form.password}
          onChange={handleInputChange('password')}
          error={errors.password}
          placeholder={t('initialization.adminAccount.passwordPlaceholder')}
          autoComplete="new-password"
          disabled={isSubmitting}
          inputClassName="w-full px-3 py-2.5 themed-input"
          showPasswordLabel={t('aria.showPassword')}
          hidePasswordLabel={t('aria.hidePassword')}
        />
        <PasswordStrengthMeter
          password={form.password}
          weakLabel={t('passwordStrength.weak')}
          mediumLabel={t('passwordStrength.medium')}
          strongLabel={t('passwordStrength.strong')}
        />
      </div>

      {/* Confirm Password Input */}
      <div>
        <PasswordField
          label={t('initialization.adminAccount.confirmPasswordLabel')}
          value={form.confirmPassword}
          onChange={handleInputChange('confirmPassword')}
          error={errors.confirmPassword}
          placeholder={t('initialization.adminAccount.confirmPasswordPlaceholder')}
          autoComplete="new-password"
          disabled={isSubmitting}
          inputClassName="w-full px-3 py-2.5 themed-input"
          showPasswordLabel={t('aria.showPassword')}
          hidePasswordLabel={t('aria.hidePassword')}
        />
      </div>

      {/* API key Input */}
      <div>
        <FormField
          label={t('initialization.adminAccount.apiKeyLabel')}
          error={errors.apiKey}
          hint={t('initialization.adminAccount.apiKeyHint')}
        >
          {(field) => (
            <input
              {...field}
              type="password"
              value={form.apiKey}
              onChange={handleInputChange('apiKey')}
              placeholder={t('initialization.adminAccount.apiKeyPlaceholder')}
              className="w-full px-3 py-2.5 themed-input"
              // `new-password` rather than `off`: browsers ignore `off` on a password input and
              // will still offer to remember the key and refill it on a later visit.
              autoComplete="new-password"
              disabled={isSubmitting}
            />
          )}
        </FormField>
      </div>

      {/* Submit Button */}
      <div className="space-y-3">
        <Button
          variant="filled"
          color="primary"
          type="submit"
          loading={isSubmitting}
          disabled={isSubmitting}
          fullWidth
        >
          {isSubmitting
            ? t(
                recovering
                  ? 'initialization.adminAccount.recovering'
                  : 'initialization.adminAccount.submitting'
              )
            : t(
                recovering
                  ? 'initialization.adminAccount.recoverSubmit'
                  : 'initialization.adminAccount.submit'
              )}
        </Button>
      </div>

      {/* Error */}
      {submitError && <Alert color="error">{submitError}</Alert>}
    </form>
  );
};
