import { noAutofill } from '@utils/autofill';
import React, { useState, useEffect, useCallback, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ConfigContext } from './ConfigContext.types';
import type { Config } from '../types';
import ApiService from '../services/api.service';
import LoadingSpinner from '../components/common/LoadingSpinner';
import { Button } from '@components/ui/Button';
import FormField from '@components/ui/FormField';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { API_BASE } from '../utils/constants';
import { getErrorMessage } from '../utils/error';
import { setServerTimezone } from '../utils/timezone';

interface ConfigProviderProps {
  children: ReactNode;
}

interface ConfigLoadError {
  message: string;
  isTimeout: boolean;
}

interface CredentialsResponse {
  success: boolean;
  message: string;
  error?: string;
}

interface PostgresPasswordRecoveryProps {
  onSaved: () => void;
}

const CONFIG_TIMEOUT_MS = 8000;

// Mirrors the server-side rule in SetupController so the button stays disabled instead of
// spending a round trip on a password the endpoint will reject.
const MIN_PASSWORD_LENGTH = 12;

// A wrong embedded-PostgreSQL password looks like a dead API, so every failure offers the way back
// in except a timeout, where nothing points at the database and the screen already says to retry.
function shouldOfferPasswordRecovery(error: ConfigLoadError): boolean {
  return !error.isTimeout;
}

/**
 * The way back in when the app cannot load its own config: set the embedded PostgreSQL
 * password again from the only screen that still renders. The API key is the proof of
 * possession here because it is checked against a file, with no database access at all.
 * Collapsed by default and inert until a key is pasted, so it cannot be triggered by a
 * stray click on what is also the screen for an ordinary network blip.
 */
const PostgresPasswordRecovery: React.FC<PostgresPasswordRecoveryProps> = ({ onSaved }) => {
  const { t } = useTranslation();
  const [isOpen, setIsOpen] = useState(false);
  const [apiKey, setApiKey] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [isSaving, setIsSaving] = useState(false);
  const [failureMessage, setFailureMessage] = useState<string | null>(null);
  const [savedMessage, setSavedMessage] = useState<string | null>(null);

  const canSubmit = apiKey.trim().length > 0 && password.length >= MIN_PASSWORD_LENGTH;

  const handleSubmit = useCallback(async (): Promise<void> => {
    if (!canSubmit || isSaving) {
      return;
    }

    setIsSaving(true);
    setFailureMessage(null);
    setSavedMessage(null);

    try {
      const response = await fetch(
        `${API_BASE}/setup/credentials`,
        ApiService.getJsonFetchOptions(
          { username: username.trim(), password },
          { method: 'POST', headers: { 'X-Api-Key': apiKey.trim() } }
        )
      );
      const data: CredentialsResponse = await response.json();

      if (response.ok && data.success) {
        setApiKey('');
        setPassword('');
        setSavedMessage(data.message || t('app.configError.recovery.saved'));
        onSaved();
      } else {
        setFailureMessage(data.error || data.message || t('app.configError.recovery.failed'));
      }
    } catch (err: unknown) {
      setFailureMessage(getErrorMessage(err) || t('app.configError.recovery.failed'));
    } finally {
      setIsSaving(false);
    }
  }, [apiKey, canSubmit, isSaving, onSaved, password, t, username]);

  if (!isOpen) {
    return (
      <div className="config-error-recovery">
        <Button
          type="button"
          variant="transparent"
          className="config-error-recovery-toggle"
          onClick={() => setIsOpen(true)}
        >
          {t('app.configError.recovery.toggle')}
        </Button>
      </div>
    );
  }

  return (
    <div className="config-error-recovery">
      <form
        className="config-error-recovery-form"
        onSubmit={(event) => {
          event.preventDefault();
          void handleSubmit();
        }}
      >
        <h3 className="config-error-recovery-heading">{t('app.configError.recovery.heading')}</h3>
        <p className="config-error-recovery-description">
          {t('app.configError.recovery.description')}
        </p>

        <div>
          <FormField label={t('modals.auth.labels.apiKey')}>
            {(field) => (
              <input
                {...noAutofill}
                {...field}
                type="password"
                className="themed-input config-error-recovery-input"
                value={apiKey}
                onChange={(event) => setApiKey(event.target.value)}
                placeholder={t('app.configError.recovery.apiKeyPlaceholder')}
                disabled={isSaving}
              />
            )}
          </FormField>
        </div>

        <div>
          <FormField label={t('app.configError.recovery.usernameLabel')}>
            {(field) => (
              <input
                {...field}
                type="text"
                className="themed-input config-error-recovery-input"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                placeholder={t('app.configError.recovery.usernamePlaceholder')}
                autoComplete="username"
                disabled={isSaving}
              />
            )}
          </FormField>
        </div>

        <div>
          <FormField label={t('app.configError.recovery.passwordLabel')}>
            {(field) => (
              <input
                {...field}
                type="password"
                className="themed-input config-error-recovery-input"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                placeholder={t('app.configError.recovery.passwordPlaceholder')}
                autoComplete="new-password"
                disabled={isSaving}
              />
            )}
          </FormField>
        </div>

        {failureMessage && (
          <p className="config-error-recovery-note" role="alert">
            {failureMessage}
          </p>
        )}
        {savedMessage && (
          <p className="config-error-recovery-note config-error-recovery-note-ok" role="status">
            {savedMessage}
          </p>
        )}

        <Button
          type="submit"
          variant="filled"
          color="primary"
          disabled={!canSubmit}
          loading={isSaving}
          stableWidth
          fullWidth
        >
          {t('app.configError.recovery.submit')}
        </Button>
      </form>
    </div>
  );
};

export const ConfigProvider: React.FC<ConfigProviderProps> = ({ children }) => {
  const { t } = useTranslation();
  const { isConnected } = useSignalR();
  const [config, setConfig] = useState<Config | null>(null);
  const [error, setError] = useState<ConfigLoadError | null>(null);
  const configRef = useRef<Config | null>(null);
  configRef.current = config;

  const loadConfig = useCallback(
    async (options?: { isRefresh?: boolean }): Promise<void> => {
      const isRefresh = options?.isRefresh ?? false;
      if (!isRefresh) {
        setError(null);
      }

      const controller = new AbortController();
      const timeoutId = window.setTimeout(() => {
        controller.abort();
      }, CONFIG_TIMEOUT_MS);

      try {
        const response = await fetch(
          `${API_BASE}/system/config`,
          ApiService.getFetchOptions({ signal: controller.signal })
        );
        const data = await ApiService.handleResponse<Config>(response);
        // Written before the children render, because this provider gates them and a consumer that
        // reads the server zone during its first render would otherwise seed itself from the
        // browser's zone and correct it a render later.
        setServerTimezone(data.timeZone);
        setConfig(data);
      } catch (err: unknown) {
        if (isRefresh && configRef.current) {
          // Background refresh - keep serving the last-good cached config. Deliberately silent;
          // not user-actionable and the app already has working config to render.
          console.warn('[ConfigProvider] Config refresh failed, keeping cached config:', err);
          return;
        }

        if (err instanceof DOMException && err.name === 'AbortError') {
          console.error('[ConfigProvider] Config request timed out after', CONFIG_TIMEOUT_MS, 'ms');
          setError({
            message: t('app.configError.timedOutMessage', {
              seconds: CONFIG_TIMEOUT_MS / 1000
            }),
            isTimeout: true
          });
        } else {
          console.error('[ConfigProvider] Failed to load config:', err);
          // Never render the raw error message - extract via the shared helper so an ApiError's
          // parsed backend body wins over a generic Error/TypeError string.
          const message = getErrorMessage(err) || t('app.configError.failedMessage');
          setError({ message, isTimeout: false });
        }
      } finally {
        window.clearTimeout(timeoutId);
      }
    },
    [t]
  );

  const refreshConfig = useCallback(async (): Promise<void> => {
    await loadConfig({ isRefresh: true });
  }, [loadConfig]);

  const updateConfig = useCallback((patch: Partial<Config>) => {
    setConfig((prev) => (prev ? { ...prev, ...patch } : prev));
  }, []);

  useEffect(() => {
    void loadConfig();
  }, [loadConfig]);

  // Nothing broadcasts a config change, so the only correction for one made while this tab was
  // disconnected is to ask again. The refresh path keeps the cached config if the request fails.
  useReconnectRefetch(isConnected, () => {
    void refreshConfig();
  });

  if (error !== null && config === null) {
    return (
      <div className="config-error-screen">
        <div className="config-error-card">
          <h2 className="config-error-title">
            {error.isTimeout
              ? t('app.configError.timedOutTitle')
              : t('app.configError.failedTitle')}
          </h2>
          <p className="config-error-message">{error.message}</p>
          <Button onClick={() => void loadConfig()}>{t('common.retry')}</Button>
          {shouldOfferPasswordRecovery(error) && (
            <PostgresPasswordRecovery onSaved={() => void loadConfig()} />
          )}
        </div>
      </div>
    );
  }

  if (!config) {
    return <LoadingSpinner fullScreen message={t('app.loading.configuration')} />;
  }

  return (
    <ConfigContext.Provider value={{ config, refreshConfig, updateConfig }}>
      {children}
    </ConfigContext.Provider>
  );
};
