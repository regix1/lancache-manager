import React, { useState, useCallback } from 'react';
import { Database, Eye, EyeOff, CheckCircle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { useAuth } from '@contexts/useAuth';
import ApiService from '@services/api.service';
import { API_BASE } from '@utils/constants';
import { getErrorMessage } from '@utils/error';
import { getPasswordStrength } from './passwordStrength';

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
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [setupSuccess, setSetupSuccess] = useState(false);

  const passwordStrength = form.password ? getPasswordStrength(form.password) : null;

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
        {/* Header */}
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
            <CheckCircle className="w-7 h-7 icon-success" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">
            {t('initialization.databaseSetup.savedHeading')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.databaseSetup.savedDescription')}
          </p>
        </div>
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
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <Database className="w-7 h-7 icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.databaseSetup.heading')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.databaseSetup.description')}
        </p>
      </div>

      {/* Username Input */}
      <div>
        <label className="form-field-label">
          {t('initialization.databaseSetup.usernameLabel')}
        </label>
        <input
          type="text"
          value={form.username}
          onChange={handleInputChange('username')}
          placeholder={t('initialization.databaseSetup.usernamePlaceholder')}
          className="w-full px-3 py-2.5 themed-input"
          autoComplete="username"
          disabled={isSubmitting}
        />
        {errors.username && <p className="text-xs text-themed-error mt-1">{errors.username}</p>}
      </div>

      {/* Password Input */}
      <div>
        <label className="form-field-label">
          {t('initialization.databaseSetup.passwordLabel')}
        </label>
        <div className="relative">
          <input
            type={showPassword ? 'text' : 'password'}
            value={form.password}
            onChange={handleInputChange('password')}
            placeholder={t('initialization.databaseSetup.passwordPlaceholder')}
            className="w-full px-3 py-2.5 pr-10 themed-input"
            autoComplete="new-password"
            disabled={isSubmitting}
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded text-themed-muted"
            onClick={() => setShowPassword((prev: boolean) => !prev)}
            tabIndex={-1}
          >
            {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
          </button>
        </div>
        {errors.password && <p className="text-xs text-themed-error mt-1">{errors.password}</p>}
        {passwordStrength && (
          <div className="flex items-center gap-2 mt-1">
            <div className="flex-1 h-1.5 rounded-full bg-themed-tertiary overflow-hidden">
              <div
                className={`h-full rounded-full transition-[width] duration-150 ${
                  passwordStrength === 'weak'
                    ? 'w-1/3 bg-[var(--theme-error-text)]'
                    : passwordStrength === 'medium'
                      ? 'w-2/3 bg-[var(--theme-warning-text)]'
                      : 'w-full bg-[var(--theme-success-text)]'
                }`}
              />
            </div>
            <span
              className={`text-xs ${
                passwordStrength === 'weak'
                  ? 'text-themed-error'
                  : passwordStrength === 'medium'
                    ? 'text-themed-warning'
                    : 'text-themed-success'
              }`}
            >
              {passwordStrength === 'weak'
                ? t('initialization.adminAccount.strengthWeak')
                : passwordStrength === 'medium'
                  ? t('initialization.adminAccount.strengthMedium')
                  : t('initialization.adminAccount.strengthStrong')}
            </span>
          </div>
        )}
      </div>

      {/* Confirm Password Input */}
      <div>
        <label className="form-field-label">
          {t('initialization.databaseSetup.confirmPasswordLabel')}
        </label>
        <div className="relative">
          <input
            type={showConfirmPassword ? 'text' : 'password'}
            value={form.confirmPassword}
            onChange={handleInputChange('confirmPassword')}
            placeholder={t('initialization.databaseSetup.confirmPasswordPlaceholder')}
            className="w-full px-3 py-2.5 pr-10 themed-input"
            autoComplete="new-password"
            disabled={isSubmitting}
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded text-themed-muted"
            onClick={() => setShowConfirmPassword((prev: boolean) => !prev)}
            tabIndex={-1}
          >
            {showConfirmPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
          </button>
        </div>
        {errors.confirmPassword && (
          <p className="text-xs text-themed-error mt-1">{errors.confirmPassword}</p>
        )}
      </div>

      {authenticationEnabled === false && (
        <div>
          <label className="form-field-label">
            {t('initialization.databaseSetup.apiKeyLabel')}
          </label>
          <input
            type="password"
            value={form.apiKey}
            onChange={handleInputChange('apiKey')}
            placeholder={t('initialization.databaseSetup.apiKeyPlaceholder')}
            className="w-full px-3 py-2.5 themed-input"
            autoComplete="off"
            disabled={isSubmitting}
          />
          {errors.apiKey && <p className="text-xs text-themed-error mt-1">{errors.apiKey}</p>}
          <p className="text-xs text-themed-muted mt-1">
            {t('initialization.databaseSetup.apiKeyHint')}
          </p>
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
      {submitError && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{submitError}</p>
        </div>
      )}
    </form>
  );
};
