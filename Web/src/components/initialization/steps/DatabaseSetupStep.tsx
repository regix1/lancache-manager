import { noAutofill } from '@utils/autofill';
import React, { useState, useCallback } from 'react';
import { Database, CheckCircle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import { PasswordField } from '@components/ui/PasswordField';
import { PasswordStrengthMeter } from '@components/ui/PasswordStrengthMeter';
import { StepHeader } from '@components/initialization/StepHeader';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import { API_BASE } from '@utils/constants';
import { getErrorMessage } from '@utils/error';

interface DatabaseSetupStepProps {
  onSetupComplete: () => void;
}

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

interface CredentialsResponse {
  success: boolean;
  message: string;
  error?: string;
}

export const DatabaseSetupStep: React.FC<DatabaseSetupStepProps> = ({ onSetupComplete }) => {
  const { t } = useTranslation();
  const { authenticationEnabled } = useAuth();
  const [form, setForm] = useState<FormState>({
    username: 'lancache',
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
  const [setupSuccess, setSetupSuccess] = useState(false);

  const validateForm = useCallback((): boolean => {
    const newErrors: FormErrors = {
      username: null,
      password: null,
      confirmPassword: null,
      apiKey: null
    };

    if (!form.username.trim()) {
      newErrors.username = t('initialization.databaseSetup.errors.usernameRequired');
    }

    if (!form.password) {
      newErrors.password = t('initialization.databaseSetup.errors.passwordRequired');
    } else if (form.password.length < 8) {
      newErrors.password = t('initialization.databaseSetup.errors.passwordTooShort');
    } else {
      const blockedPasswords = [
        'lancache',
        'password',
        '12345678',
        'admin123',
        'qwerty123',
        'lancache1',
        'lancache123'
      ];
      if (blockedPasswords.includes(form.password.toLowerCase())) {
        newErrors.password = t('initialization.databaseSetup.errors.passwordTooCommon');
      }

      const username = form.username?.trim() || 'lancache';
      if (form.password.toLowerCase() === username.toLowerCase()) {
        newErrors.password = t('initialization.databaseSetup.errors.passwordSameAsUsername');
      }
    }

    if (!form.confirmPassword) {
      newErrors.confirmPassword = t('initialization.databaseSetup.errors.confirmPasswordRequired');
    } else if (form.password !== form.confirmPassword) {
      newErrors.confirmPassword = t('initialization.databaseSetup.errors.passwordsDoNotMatch');
    }

    if (authenticationEnabled === false && !form.apiKey.trim()) {
      newErrors.apiKey = t('initialization.apiKey.errors.required');
    }

    setErrors(newErrors);
    return Object.values(newErrors).every((error) => error === null);
  }, [authenticationEnabled, form, t]);

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
        `${API_BASE}/setup/credentials`,
        ApiService.getJsonFetchOptions(
          {
            username: form.username.trim(),
            password: form.password
          },
          {
            method: 'POST',
            ...(authenticationEnabled === false
              ? { headers: { 'X-Api-Key': form.apiKey.trim() } }
              : {})
          }
        )
      );

      const data: CredentialsResponse = await response.json();

      if (response.ok && data.success) {
        setSetupSuccess(true);
        setTimeout(() => {
          onSetupComplete();
        }, 1500);
      } else {
        setSubmitError(
          data.error || data.message || t('initialization.databaseSetup.errors.saveFailed')
        );
      }
    } catch (error: unknown) {
      setSubmitError(getErrorMessage(error) || t('initialization.databaseSetup.errors.network'));
    } finally {
      setIsSubmitting(false);
    }
  }, [authenticationEnabled, form, validateForm, onSetupComplete, t]);

  if (setupSuccess) {
    return (
      <div className="space-y-5">
        <StepHeader
          icon={<CheckCircle className="w-7 h-7 icon-success" />}
          iconBackground="bg-themed-success"
          title={t('initialization.databaseSetup.savedHeading')}
          description={t('initialization.databaseSetup.savedDescription')}
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
        icon={<Database className="w-7 h-7 icon-info" />}
        iconBackground="bg-themed-info"
        title={t('initialization.databaseSetup.heading')}
        description={t('initialization.databaseSetup.description')}
      />

      {/* Username Input */}
      <div>
        <FormField label={t('initialization.databaseSetup.usernameLabel')} error={errors.username}>
          {(field) => (
            <input
              {...field}
              type="text"
              value={form.username}
              onChange={handleInputChange('username')}
              placeholder={t('initialization.databaseSetup.usernamePlaceholder')}
              className="themed-input setup-input"
              autoComplete="username"
              disabled={isSubmitting}
            />
          )}
        </FormField>
      </div>

      {/* Password Input */}
      <div>
        <PasswordField
          label={t('initialization.databaseSetup.passwordLabel')}
          value={form.password}
          onChange={handleInputChange('password')}
          error={errors.password}
          placeholder={t('initialization.databaseSetup.passwordPlaceholder')}
          autoComplete="new-password"
          disabled={isSubmitting}
          inputClassName="themed-input setup-input"
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
          label={t('initialization.databaseSetup.confirmPasswordLabel')}
          value={form.confirmPassword}
          onChange={handleInputChange('confirmPassword')}
          error={errors.confirmPassword}
          placeholder={t('initialization.databaseSetup.confirmPasswordPlaceholder')}
          autoComplete="new-password"
          disabled={isSubmitting}
          inputClassName="themed-input setup-input"
          showPasswordLabel={t('aria.showPassword')}
          hidePasswordLabel={t('aria.hidePassword')}
        />
      </div>

      {authenticationEnabled === false && (
        <div>
          <FormField
            label={t('initialization.databaseSetup.apiKeyLabel')}
            error={errors.apiKey}
            hint={t('initialization.databaseSetup.apiKeyHint')}
          >
            {(field) => (
              <input
                {...noAutofill}
                {...field}
                type="password"
                value={form.apiKey}
                onChange={handleInputChange('apiKey')}
                placeholder={t('initialization.databaseSetup.apiKeyPlaceholder')}
                className="themed-input setup-input"
                disabled={isSubmitting}
              />
            )}
          </FormField>
        </div>
      )}

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
            ? t('initialization.databaseSetup.submitting')
            : t('initialization.databaseSetup.submit')}
        </Button>
      </div>

      {/* Info Box */}
      <div className="p-3 rounded-lg text-sm bg-themed-tertiary">
        <p className="text-themed-secondary">
          <strong className="text-themed-primary">
            {t('initialization.databaseSetup.infoTitle')}
          </strong>{' '}
          {t('initialization.databaseSetup.infoBody')}
        </p>
      </div>

      {/* Error */}
      {submitError && <Alert color="error">{submitError}</Alert>}
    </form>
  );
};
