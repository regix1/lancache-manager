import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Key } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { getErrorMessage } from '@utils/error';

const AuthenticateTab: React.FC = () => {
  const { t } = useTranslation();
  const { refreshAuth } = useAuth();
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const [apiKey, setApiKey] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  // Shown on the form itself. A refusal used to go only to a notification, which on a phone is
  // gone before it is read and on this screen never appeared at all, so a wrong password looked
  // like a button that did nothing.
  const [authError, setAuthError] = useState<string | null>(null);

  const notifySuccess = (message: string) => {
    addNotification({
      type: 'generic',
      status: 'completed',
      message,
      details: { notificationType: 'success' }
    });
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      setAuthError(t('auth.errors.missingKey'));
      return;
    }

    setLoading(true);
    setAuthError(null);

    try {
      const result = await authService.login(apiKey, username.trim(), password);

      if (result.success) {
        setAuthError(null);
        notifySuccess(t('auth.success'));
        await refreshAuth();
        setApiKey('');
        setUsername('');
        setPassword('');

        setTimeout(() => {
          window.location.reload();
        }, 1500);
      } else {
        // The server answers every credential refusal the same way on purpose, so this names all
        // three fields rather than the one that was wrong. Telling them apart is what lets somebody
        // work out which usernames exist.
        setAuthError(result.message || t('auth.errors.failed'));
      }
    } catch (err: unknown) {
      setAuthError(getErrorMessage(err) || t('auth.errors.failed'));
      notifyError(t('auth.errors.failed'), err, { logLabel: 'Authentication error' });
    } finally {
      setLoading(false);
    }
  };

  const credentialsFilled = apiKey.trim() !== '' && username.trim() !== '' && password !== '';

  const handleCredentialKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' && credentialsFilled && !loading) {
      handleAuthenticate();
    }
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6">
      <div className="auth-upgrade">
        <Card padding="none" className="auth-upgrade-card">
          <div className="auth-upgrade-header">
            <div className="icon-box icon-box--md auth-upgrade-icon">
              <Key className="w-5 h-5" />
            </div>
            <div>
              <h1 className="auth-upgrade-title">{t('auth.header.title')}</h1>
              <p className="auth-upgrade-subtitle">{t('auth.header.subtitle')}</p>
            </div>
          </div>

          <div className="auth-upgrade-form">
            <div>
              <label className="form-field-label">{t('auth.form.label')}</label>
              <input
                type="password"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                onKeyDown={handleCredentialKeyDown}
                placeholder={t('auth.form.placeholder')}
                className="w-full p-3 text-sm themed-input"
                autoComplete="off"
                disabled={loading}
              />
            </div>

            <div>
              <label className="form-field-label">{t('modals.auth.labels.username')}</label>
              <input
                type="text"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                onKeyDown={handleCredentialKeyDown}
                placeholder={t('modals.auth.placeholders.enterUsername')}
                className="w-full p-3 text-sm themed-input"
                autoComplete="username"
                disabled={loading}
              />
            </div>

            <div>
              <label className="form-field-label">{t('modals.auth.labels.password')}</label>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                onKeyDown={handleCredentialKeyDown}
                placeholder={t('modals.auth.placeholders.enterPassword')}
                className="w-full p-3 text-sm themed-input"
                autoComplete="current-password"
                disabled={loading}
              />
            </div>

            {authError && <Alert color="red">{authError}</Alert>}

            <Button
              variant="filled"
              color="blue"
              size="md"
              onClick={handleAuthenticate}
              loading={loading}
              disabled={!credentialsFilled || loading}
              fullWidth
            >
              {t('auth.form.submit')}
            </Button>
          </div>

          <div className="auth-upgrade-help">
            <p className="auth-upgrade-help-title">{t('auth.help.title')}</p>
            <ol className="auth-upgrade-help-list">
              <li>{t('auth.help.step1')}</li>
              <li>
                {t('auth.help.step2.before')}
                <code>{t('auth.help.step2.code')}</code>
              </li>
              <li>
                {t('auth.help.step3.before')} <code>{t('auth.help.step3.code')}</code>
              </li>
            </ol>
          </div>
        </Card>
      </div>
    </div>
  );
};

export default AuthenticateTab;
