import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Key, Lock } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useNotifications } from '@contexts/notifications';
import { useErrorHandler } from '@hooks/useErrorHandler';

const AuthenticateTab: React.FC = () => {
  const { t } = useTranslation();
  const { refreshAuth } = useAuth();
  const { addNotification } = useNotifications();
  const { notifyError } = useErrorHandler();
  const [apiKey, setApiKey] = useState('');
  const [loading, setLoading] = useState(false);

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
      notifyError(t('auth.errors.missingKey'));
      return;
    }

    setLoading(true);

    try {
      const result = await authService.login(apiKey);

      if (result.success) {
        notifySuccess(t('auth.success'));
        // Refresh auth context
        await refreshAuth();
        // Clear input
        setApiKey('');

        // Give user a moment to see success message before redirect happens
        setTimeout(() => {
          window.location.reload();
        }, 1500);
      } else {
        notifyError(result.message || t('auth.errors.failed'));
      }
    } catch (err: unknown) {
      notifyError(t('auth.errors.failed'), err, { logLabel: 'Authentication error' });
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6">
      <div className="max-w-lg mx-auto space-y-4 sm:space-y-6">
        {/* Header */}
        <div className="flex items-center gap-3">
          <div className="p-2 rounded-lg flex-shrink-0 bg-themed-accent-subtle">
            <Key className="w-6 h-6 text-themed-accent" />
          </div>
          <div className="min-w-0">
            <h1 className="text-xl sm:text-2xl font-bold text-themed-primary">
              {t('auth.header.title')}
            </h1>
          </div>
        </div>

        {/* Authentication Card */}
        <Card>
          <div className="p-6 space-y-6">
            <div className="space-y-4">
              <div>
                <label className="form-field-label">{t('auth.form.label')}</label>
                <input
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && handleAuthenticate()}
                  placeholder={t('auth.form.placeholder')}
                  className="w-full px-3 py-2 themed-input text-themed-primary placeholder-themed-muted focus:outline-none bg-themed-secondary border border-themed rounded-lg"
                  disabled={loading}
                />
              </div>

              <Button
                variant="filled"
                color="green"
                size="md"
                leftSection={<Lock className="w-4 h-4" />}
                onClick={handleAuthenticate}
                loading={loading}
                disabled={!apiKey.trim() || loading}
                className="w-full sm:w-auto"
              >
                {t('auth.form.submit')}
              </Button>
            </div>

            <Alert color="blue">
              <div>
                <p className="font-medium mb-2">{t('auth.help.title')}</p>
                <ol className="list-decimal list-inside text-sm space-y-1 ml-2">
                  <li>{t('auth.help.step1')}</li>
                  <li>
                    {t('auth.help.step2.before')}
                    <code className="bg-themed-tertiary px-1 rounded">
                      {t('auth.help.step2.code')}
                    </code>
                  </li>
                  <li>
                    {t('auth.help.step3.before')}
                    <code className="bg-themed-tertiary px-1 rounded">
                      {t('auth.help.step3.code')}
                    </code>
                  </li>
                </ol>
              </div>
            </Alert>
          </div>
        </Card>
      </div>
    </div>
  );
};

export default AuthenticateTab;
