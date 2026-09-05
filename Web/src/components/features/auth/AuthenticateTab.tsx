import React, { useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { Key } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import CredentialFields from '@components/ui/CredentialFields';
import type { CredentialField } from '@components/ui/CredentialFields.types';
import { LoginServiceButtons } from './LoginServiceButtons';
import authService from '@services/auth.service';
import { requiresApiKey, usesOidc } from '@utils/accountMode';
import { signInServices, type LoginService } from '@utils/loginService';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler, useNotifySuccess } from '@hooks/useErrorHandler';
import { getErrorMessage } from '@utils/error';

const AuthenticateTab: React.FC = () => {
  const { t } = useTranslation();
  const { refreshAuth, accountMode, oidcDisplayName, loginServices } = useAuth();
  const { notifyError } = useErrorHandler();
  const { notifySuccess } = useNotifySuccess();
  const [apiKey, setApiKey] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [startingService, setStartingService] = useState<string | null>(null);
  // Shown on the form itself. A refusal used to go only to a notification, which on a phone is
  // gone before it is read and on this screen never appeared at all, so a wrong password looked
  // like a button that did nothing.
  const [authError, setAuthError] = useState<string | null>(null);

  const handleAuthenticate = async () => {
    if (requiresApiKey(accountMode) && !apiKey.trim()) {
      setAuthError(t('auth.errors.missingKey'));
      return;
    }

    setLoading(true);
    setAuthError(null);

    try {
      if (usesOidc(accountMode)) {
        const result = await authService.startOidc(apiKey.trim());
        window.location.assign(result.url);
        return;
      }
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

  const credentialsFilled =
    (!requiresApiKey(accountMode) || apiKey.trim() !== '') &&
    (usesOidc(accountMode) || (username.trim() !== '' && password !== ''));

  // One button per active connection; the legacy single custom OpenID Connect entry advertises
  // none and keeps the compatibility button below.
  const services = signInServices(loginServices, accountMode);
  const startService = async (service: LoginService) => {
    if (requiresApiKey(accountMode) && !apiKey.trim()) {
      setAuthError(t('auth.errors.missingKey'));
      return;
    }
    setStartingService(service.id);
    setAuthError(null);
    try {
      const result = await authService.startLogin(service.id, apiKey.trim());
      window.location.assign(result.url);
    } catch (err: unknown) {
      setAuthError(getErrorMessage(err) || t('auth.errors.failed'));
      setStartingService(null);
    }
  };

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
    <div className="auth-upgrade">
      <Card padding="none" className="auth-upgrade-card">
        <div className="auth-upgrade-header">
          <div className="icon-box icon-box--md auth-upgrade-icon">
            <Key className="w-5 h-5" />
          </div>
          <div>
            <h1 className="auth-upgrade-title">{t('auth.header.title')}</h1>
            <p className="auth-upgrade-subtitle">{t(`accessSetup.login.${accountMode}`)}</p>
          </div>
        </div>

        <form
          className="auth-upgrade-form"
          method="post"
          onSubmit={(event) => {
            event.preventDefault();
            if (
              credentialsFilled &&
              !loading &&
              startingService === null &&
              services.length === 0
            ) {
              void handleAuthenticate();
            }
          }}
        >
          <CredentialFields
            accountMode={accountMode}
            apiKey={apiKey}
            username={username}
            password={password}
            onChange={handleCredentialChange}
            onSubmit={handleAuthenticate}
            disabled={loading}
            apiKeyPlaceholder={t('auth.form.placeholder')}
          />

          {authError && <Alert color="red">{authError}</Alert>}

          {services.length > 0 ? (
            <LoginServiceButtons
              services={services}
              starting={startingService}
              disabled={startingService !== null || !credentialsFilled}
              onStart={(service) => void startService(service)}
            />
          ) : (
            <Button
              variant="filled"
              color="primary"
              size="md"
              type="submit"
              loading={loading}
              disabled={!credentialsFilled || loading}
              fullWidth
            >
              {usesOidc(accountMode)
                ? t('accessSetup.signInSso', { name: oidcDisplayName || t('accessSetup.sso') })
                : t('auth.form.submit')}
            </Button>
          )}
        </form>

        {requiresApiKey(accountMode) && (
          <div className="auth-upgrade-help">
            <p className="auth-upgrade-help-title">{t('auth.help.title')}</p>
            <dl className="auth-upgrade-help-defs">
              <div>
                <dt>{t('auth.help.fileLabel')}</dt>
                <dd>
                  <code>{t('auth.help.filePath')}</code>
                </dd>
              </div>
              <div>
                <dt>{t('auth.help.logsLabel')}</dt>
                <dd>
                  <code>{t('auth.help.logsCommand')}</code>
                </dd>
              </div>
            </dl>
            <div className="auth-upgrade-help-recovery">
              <p className="auth-upgrade-help-recovery-title">{t('auth.help.forgotTitle')}</p>
              <p>
                <Trans
                  i18nKey="auth.help.forgotPassword"
                  components={{
                    code: <code />,
                    docs: (
                      <a
                        className="auth-upgrade-docs"
                        href={t('auth.help.docsHref')}
                        target="_blank"
                        rel="noopener noreferrer"
                      />
                    )
                  }}
                />
              </p>
            </div>
          </div>
        )}
      </Card>
    </div>
  );
};

export default AuthenticateTab;
