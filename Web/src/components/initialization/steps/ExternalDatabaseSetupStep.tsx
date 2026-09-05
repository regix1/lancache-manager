import React, { useState, useCallback } from 'react';
import { Database, CheckCircle, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import { PostgresConnectionFields } from '@components/ui/PostgresConnectionFields';
import type { PostgresConnectionField } from '@components/ui/PostgresConnectionFields.types';
import { StepHeader } from '@components/initialization/StepHeader';
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

  const handlePostgresFieldChange = useCallback((field: PostgresConnectionField, value: string) => {
    setForm((prev) => ({ ...prev, [field]: value }));
    setErrors((prev) => ({ ...prev, [field]: null }));
    setSubmitError(null);
  }, []);

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
        <StepHeader
          icon={<CheckCircle className="w-7 h-7 icon-success" />}
          iconBackground="bg-themed-success"
          title={t('initialization.externalDb.successTitle')}
          description={t('initialization.externalDb.successBody')}
        />

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
      <StepHeader
        icon={<Database className="w-7 h-7 icon-primary" />}
        iconBackground="bg-themed-tertiary"
        title={t('initialization.externalDb.title')}
        description={t('initialization.externalDb.body')}
      />

      <PostgresConnectionFields
        values={{
          host: form.host,
          port: form.port,
          database: form.database,
          username: form.username,
          password: form.password
        }}
        labels={{
          host: t('initialization.postgresFields.host'),
          port: t('initialization.postgresFields.port'),
          database: t('initialization.postgresFields.database'),
          username: t('initialization.postgresFields.username'),
          password: t('initialization.postgresFields.password')
        }}
        errors={{
          host: errors.host,
          port: errors.port,
          database: errors.database,
          username: errors.username,
          password: errors.password
        }}
        onFieldChange={handlePostgresFieldChange}
        inputClassName="themed-input setup-input"
        disabled={isSubmitting}
        passwordReveal={{ showLabel: t('aria.showPassword'), hideLabel: t('aria.hidePassword') }}
      />

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
              // `new-password` rather than `off`: browsers ignore `off` on a password input and
              // will still offer to remember the key and refill it on a later visit.
              autoComplete="new-password"
              className="themed-input setup-input"
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
