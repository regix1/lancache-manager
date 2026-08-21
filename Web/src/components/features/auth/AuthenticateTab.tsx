import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Key } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import CredentialFields from '@components/ui/CredentialFields';
import type { CredentialField } from '@components/ui/CredentialFields.types';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler, useNotifySuccess } from '@hooks/useErrorHandler';
import { getErrorMessage } from '@utils/error';

const AuthenticateTab: React.FC = () => {
  const { t } = useTranslation();
  const { refreshAuth } = useAuth();
  const { notifyError } = useErrorHandler();
  const { notifySuccess } = useNotifySuccess();
  const [apiKey, setApiKey] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  // Shown on the form itself. A refusal used to go only to a notification, which on a phone is
  // gone before it is read and on this screen never appeared at all, so a wrong password looked
  // like a button that did nothing.
  const [authError, setAuthError] = useState<string | null>(null);

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

  const handleCredentialChange = (field: CredentialField, value: string) => {
    if (field === 'apiKey') {
      setApiKey(value);
    } else if (field === 'username') {
      setUsername(value);
    } else {
      setPassword(value);
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
            <CredentialFields
              apiKey={apiKey}
              username={username}
              password={password}
              onChange={handleCredentialChange}
              onSubmit={handleAuthenticate}
              disabled={loading}
              apiKeyPlaceholder={t('auth.form.placeholder')}
            />

            {authError && <Alert color="red">{authError}</Alert>}

            <Button
              variant="filled"
              color="primary"
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
            {/* Points at the container log rather than offering a field here. Recovery is proved by
                the API key, and a form on this screen would be somewhere to paste the one secret
                that owns the installation. Whoever can read that log is already who may recover. */}
            <p className="auth-upgrade-help-recovery">{t('auth.help.forgotPassword')}</p>
          </div>
        </Card>
      </div>
    </div>
  );
};

export default AuthenticateTab;
