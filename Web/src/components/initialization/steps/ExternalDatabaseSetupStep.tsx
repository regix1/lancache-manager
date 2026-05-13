import React, { useState, useCallback } from 'react';
import { Database, Eye, EyeOff, CheckCircle, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';

interface ExternalDatabaseSetupStepProps {
  onSetupComplete: () => void;
}

interface FormState {
  host: string;
  port: string;
  database: string;
  username: string;
  password: string;
}

interface FormErrors {
  host: string | null;
  port: string | null;
  database: string | null;
  username: string | null;
  password: string | null;
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
    password: ''
  });
  const [errors, setErrors] = useState<FormErrors>({
    host: null,
    port: null,
    database: null,
    username: null,
    password: null
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
      password: null
    };

    if (!form.host.trim()) next.host = 'Host is required';
    const portNum = Number.parseInt(form.port, 10);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum > 65535) {
      next.port = 'Port must be between 1 and 65535';
    }
    if (!form.database.trim()) next.database = 'Database name is required';
    if (!form.username.trim()) next.username = 'Username is required';
    if (!form.password) next.password = 'Password is required';

    setErrors(next);
    return Object.values(next).every((v) => v === null);
  }, [form]);

  const handleSubmit = useCallback(async () => {
    if (!validateForm()) return;
    setIsSubmitting(true);
    setSubmitError(null);

    try {
      const result = await ApiService.setExternalDbCredentials({
        host: form.host.trim(),
        port: Number.parseInt(form.port, 10),
        database: form.database.trim(),
        username: form.username.trim(),
        password: form.password
      });

      if (result.success) {
        setSetupSuccess(true);
        onSetupComplete();
      } else {
        setSubmitError(result.error || result.message || 'Failed to save credentials.');
      }
    } catch (err) {
      const message =
        err instanceof Error ? err.message : 'Network error. Please check your connection.';
      setSubmitError(message);
    } finally {
      setIsSubmitting(false);
    }
  }, [form, validateForm, onSetupComplete]);

  if (setupSuccess) {
    return (
      <div className="space-y-5">
        <div className="flex flex-col items-center text-center">
          <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-success">
            <CheckCircle className="w-7 h-7 icon-success" />
          </div>
          <h3 className="text-lg font-semibold text-themed-primary mb-1">
            {t('initialization.externalDb.successTitle', 'Credentials Saved')}
          </h3>
          <p className="text-sm text-themed-secondary max-w-md">
            {t(
              'initialization.externalDb.successBody',
              'The external PostgreSQL connection has been saved. Restart the container to apply.'
            )}
          </p>
        </div>

        <div className="rounded-lg border border-themed-secondary bg-themed-tertiary p-4 flex items-start gap-3">
          <RefreshCw className="w-5 h-5 mt-0.5 icon-primary flex-shrink-0" />
          <div className="text-sm text-themed-secondary">
            {t(
              'initialization.externalDb.restartHint',
              'Run "docker compose restart lancache-manager" (or restart the container however you started it). After it comes back up, the wizard will resume from the next step.'
            )}
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
          {t('initialization.externalDb.title', 'External PostgreSQL Connection')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t(
            'initialization.externalDb.body',
            'POSTGRES_MODE is set to external but connection details were not provided via environment variables. Enter the details for your PostgreSQL server below.'
          )}
        </p>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div className="sm:col-span-2">
          <label className="block text-xs font-medium text-themed-secondary mb-1">
            {t('initialization.externalDb.fields.host', 'Host')}
          </label>
          <input
            type="text"
            value={form.host}
            onChange={handleFieldChange('host')}
            placeholder="lancache-db"
            disabled={isSubmitting}
            className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
          />
          {errors.host && <p className="text-xs text-themed-error mt-1">{errors.host}</p>}
        </div>
        <div>
          <label className="block text-xs font-medium text-themed-secondary mb-1">
            {t('initialization.externalDb.fields.port', 'Port')}
          </label>
          <input
            type="text"
            inputMode="numeric"
            value={form.port}
            onChange={handleFieldChange('port')}
            placeholder="5432"
            disabled={isSubmitting}
            className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
          />
          {errors.port && <p className="text-xs text-themed-error mt-1">{errors.port}</p>}
        </div>
      </div>

      <div>
        <label className="block text-xs font-medium text-themed-secondary mb-1">
          {t('initialization.externalDb.fields.database', 'Database')}
        </label>
        <input
          type="text"
          value={form.database}
          onChange={handleFieldChange('database')}
          placeholder="lancache"
          disabled={isSubmitting}
          className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
        />
        {errors.database && <p className="text-xs text-themed-error mt-1">{errors.database}</p>}
      </div>

      <div>
        <label className="block text-xs font-medium text-themed-secondary mb-1">
          {t('initialization.externalDb.fields.username', 'Username')}
        </label>
        <input
          type="text"
          value={form.username}
          onChange={handleFieldChange('username')}
          placeholder="lancache"
          disabled={isSubmitting}
          className="w-full px-3 py-2 rounded-md border border-themed-secondary bg-themed-tertiary text-themed-primary text-sm"
        />
        {errors.username && <p className="text-xs text-themed-error mt-1">{errors.username}</p>}
      </div>

      <div>
        <label className="block text-xs font-medium text-themed-secondary mb-1">
          {t('initialization.externalDb.fields.password', 'Password')}
        </label>
        <div className="relative">
          <input
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
        {errors.password && <p className="text-xs text-themed-error mt-1">{errors.password}</p>}
      </div>

      {submitError && (
        <div className="rounded-md p-3 text-sm bg-themed-error text-themed-error">
          {submitError}
        </div>
      )}

      <Button variant="default" onClick={handleSubmit} disabled={isSubmitting} className="w-full">
        {isSubmitting
          ? t('initialization.externalDb.testingConnection', 'Testing connection...')
          : t('initialization.externalDb.submit', 'Test and Save Connection')}
      </Button>

      <p className="text-xs text-themed-muted text-center">
        {t(
          'initialization.externalDb.tip',
          'Credentials are validated against the live server before saving. They are written to /data/config/postgres-credentials.json.'
        )}
      </p>
    </div>
  );
};
