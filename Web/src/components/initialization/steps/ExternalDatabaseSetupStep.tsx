import React, { useState, useCallback } from 'react';
import { Database, Eye, EyeOff, CheckCircle, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';

interface ExternalDatabaseSetupStepProps {
  onSetupComplete: () => void;
}

interface FormState {
  host: string;
  port: string;
  database: string;
  username: string;
  password: string;
  apiKey: string;
}

interface FormErrors {
  host: string | null;
  port: string | null;
  database: string | null;
  username: string | null;
  password: string | null;
  apiKey: string | null;
}

export const ExternalDatabaseSetupStep: React.FC<ExternalDatabaseSetupStepProps> = ({
  onSetupComplete
}) => {
  const { t } = useTranslation();
  const [form, setForm] = useState<FormState>({
    host: '',
    port: '5432',
    database: 'lancache',
    username: 'lancache',
    password: '',
    apiKey: ''
  });
  const [errors, setErrors] = useState<FormErrors>({
    host: null,
    port: null,
    database: null,
    username: null,
    password: null,
    apiKey: null
  });
  const [showPassword, setShowPassword] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [setupSuccess, setSetupSuccess] = useState(false);

  const handleFieldChange = useCallback(
    (field: keyof FormState) => (e: React.ChangeEvent<HTMLInputElement>) => {
      setForm((prev) => ({ ...prev, [field]: e.target.value }));
      setErrors((prev) => ({ ...prev, [field]: null }));
      setSubmitError(null);
    },
    []
  );

  const validateForm = useCallback((): boolean => {
    const next: FormErrors = {
      host: null,
      port: null,
      database: null,
      username: null,
      password: null,
      apiKey: null
    };

    if (!form.host.trim()) next.host = t('initialization.externalDb.errors.hostRequired');
    const portNum = Number.parseInt(form.port, 10);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum > 65535) {
      next.port = t('initialization.externalDb.errors.portRange');
    }
    if (!form.database.trim())
      next.database = t('initialization.externalDb.errors.databaseRequired');
    if (!form.username.trim())
      next.username = t('initialization.externalDb.errors.usernameRequired');
    if (!form.password) next.password = t('initialization.externalDb.errors.passwordRequired');
    if (!form.apiKey.trim()) next.apiKey = t('initialization.apiKey.errors.required');

    setErrors(next);
    return Object.values(next).every((v) => v === null);
  }, [form, t]);

  const handleSubmit = useCallback(async () => {
    if (!validateForm()) return;
    setIsSubmitting(true);
    setSubmitError(null);

    try {
      const result = await ApiService.setExternalDbCredentials(
        {
          host: form.host.trim(),
          port: Number.parseInt(form.port, 10),
          database: form.database.trim(),
          username: form.username.trim(),
          password: form.password
        },
        form.apiKey.trim()
      );

      if (result.success) {
        setSetupSuccess(true);
        onSetupComplete();
      } else {
        setSubmitError(
          result.error || result.message || t('initialization.externalDb.errors.saveFailed')
        );
      }
    } catch (err) {
      setSubmitError(getErrorMessage(err) || t('initialization.externalDb.errors.network'));
    } finally {
      setIsSubmitting(false);
    }
  }, [form, validateForm, onSetupComplete, t]);

  if (setupSuccess) {
    return (
      <div className="space-y-5">
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
            <CheckCircle className="w-7 h-7 icon-success" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">
            {t('initialization.externalDb.successTitle')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t('initialization.externalDb.successBody')}
          </p>
        </div>

        <div className="rounded-lg border border-themed-secondary bg-themed-tertiary p-4 flex items-start gap-3">
          <RefreshCw className="w-5 h-5 mt-0.5 icon-primary flex-shrink-0" />
          <div className="text-sm text-themed-secondary">
            {t('initialization.externalDb.restartHint')}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-tertiary">
          <Database className="w-7 h-7 icon-primary" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.externalDb.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.externalDb.body')}
        </p>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div className="sm:col-span-2">
          <FormField label={t('initialization.externalDb.fields.host')} error={errors.host}>
            {(field) => (
              <input
                {...field}
                type="text"
                value={form.host}
                onChange={handleFieldChange('host')}
                placeholder="lancache-db"
                disabled={isSubmitting}
                className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
              />
            )}
          </FormField>
        </div>
        <div>
          <FormField label={t('initialization.externalDb.fields.port')} error={errors.port}>
            {(field) => (
              <input
                {...field}
                type="text"
                inputMode="numeric"
                value={form.port}
                onChange={handleFieldChange('port')}
                placeholder="5432"
                disabled={isSubmitting}
                className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
              />
            )}
          </FormField>
        </div>
      </div>

      <div>
        <FormField label={t('initialization.externalDb.fields.database')} error={errors.database}>
          {(field) => (
            <input
              {...field}
              type="text"
              value={form.database}
              onChange={handleFieldChange('database')}
              placeholder="lancache"
              disabled={isSubmitting}
              className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
            />
          )}
        </FormField>
      </div>

      <div>
        <FormField label={t('initialization.externalDb.fields.username')} error={errors.username}>
          {(field) => (
            <input
              {...field}
              type="text"
              value={form.username}
              onChange={handleFieldChange('username')}
              placeholder="lancache"
              disabled={isSubmitting}
              className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
            />
          )}
        </FormField>
      </div>

      <div>
        <FormField label={t('initialization.externalDb.fields.password')} error={errors.password}>
          {(field) => (
            <div className="relative">
              <input
                {...field}
                type={showPassword ? 'text' : 'password'}
                value={form.password}
                onChange={handleFieldChange('password')}
                disabled={isSubmitting}
                className="w-full px-3 py-2 pr-10 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
              />
              <button
                type="button"
                onClick={() => setShowPassword((s) => !s)}
                className="absolute right-2 top-1/2 -translate-y-1/2 text-themed-secondary"
                tabIndex={-1}
              >
                {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
              </button>
            </div>
          )}
        </FormField>
      </div>

      <div>
        <FormField
          label={t('initialization.apiKey.label')}
          error={errors.apiKey}
          hint={t('initialization.externalDb.apiKeyHelp')}
        >
          {(field) => (
            <input
              {...field}
              type="password"
              value={form.apiKey}
              onChange={handleFieldChange('apiKey')}
              placeholder={t('initialization.apiKey.placeholder')}
              disabled={isSubmitting}
              autoComplete="off"
              className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
            />
          )}
        </FormField>
      </div>

      {submitError && <Alert color="error">{submitError}</Alert>}

      <Button variant="default" onClick={handleSubmit} disabled={isSubmitting} className="w-full">
        {isSubmitting
          ? t('initialization.externalDb.testingConnection')
          : t('initialization.externalDb.submit')}
      </Button>

      <p className="text-xs text-themed-muted text-center">{t('initialization.externalDb.tip')}</p>
    </div>
  );
};
