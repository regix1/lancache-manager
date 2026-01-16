import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Key, Lock, AlertCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Alert } from '@components/ui/Alert';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/AuthContext';

const AuthenticateTab: React.FC = () => {
  const { t } = useTranslation();
  const { refreshAuth } = useAuth();
  const [apiKey, setApiKey] = useState('');
  const [loading, setLoading] = useState(false);

  // Helper to show toast notifications
  const showToast = (type: 'success' | 'error' | 'info', message: string) => {
    window.dispatchEvent(new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    }));
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      showToast('error', t('auth.errors.missingKey'));
      return;
    }

    setLoading(true);

    try {
      const result = await authService.register(apiKey);

      if (result.success) {
        showToast('success', t('auth.success'));
        // Refresh auth context
        await refreshAuth();
        // Clear input
        setApiKey('');

        // Give user a moment to see success message before redirect happens
        setTimeout(() => {
          window.location.reload();
        }, 1500);
      } else {
        showToast('error', result.message || t('auth.errors.failed'));
      }
    } catch (err: unknown) {
      console.error('Authentication error:', err);
      showToast('error', (err instanceof Error ? err.message : String(err)) || t('auth.errors.failed'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-4 sm:space-y-6">
      {/* Header */}
      <div className="flex items-center gap-3">
        <div className="p-2 rounded-lg flex-shrink-0 bg-themed-accent-subtle">
          <Key className="w-6 h-6 text-themed-accent" />
        </div>
        <div className="min-w-0">
          <h1 className="text-xl sm:text-2xl font-bold text-themed-primary">
            {t('auth.header.title')}
          </h1>
          <p className="text-xs sm:text-sm text-themed-secondary">
            {t('auth.header.subtitle')}
          </p>
        </div>
      </div>

      {/* Authentication Card */}
      <Card>
        <div className="p-6 space-y-6">
          <div>
            <h2 className="text-lg font-semibold mb-4 text-themed-primary">
              {t('auth.form.title')}
            </h2>
            <p className="text-sm mb-4 text-themed-secondary">
              {t('auth.form.subtitle.before')}
              <strong>{t('auth.form.subtitle.emphasis')}</strong>
              {t('auth.form.subtitle.after')}
            </p>

            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium mb-2 text-themed-secondary">
                  {t('auth.form.label')}
                </label>
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
          </div>

          <Alert color="blue">
            <div>
              <p className="font-medium mb-2">{t('auth.help.title')}</p>
              <ol className="list-decimal list-inside text-sm space-y-1 ml-2">
                <li>{t('auth.help.step1')}</li>
                <li>
                  {t('auth.help.step2.before')}
                  <code className="bg-themed-tertiary px-1 rounded">{t('auth.help.step2.code')}</code>
                </li>
                <li>
                  {t('auth.help.step3.before')}
                  <code className="bg-themed-tertiary px-1 rounded">{t('auth.help.step3.code')}</code>
                </li>
              </ol>
            </div>
          </Alert>
        </div>
      </Card>

      {/* Features Card */}
      <Card>
        <div className="p-6">
          <h2 className="text-lg font-semibold mb-4 text-themed-primary">
            {t('auth.features.title')}
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  {t('auth.features.userManagement.title')}
                </h3>
                <p className="text-sm text-themed-secondary">
                  {t('auth.features.userManagement.description')}
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  {t('auth.features.cacheManagement.title')}
                </h3>
                <p className="text-sm text-themed-secondary">
                  {t('auth.features.cacheManagement.description')}
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  {t('auth.features.databaseOperations.title')}
                </h3>
                <p className="text-sm text-themed-secondary">
                  {t('auth.features.databaseOperations.description')}
                </p>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="p-2 rounded-lg flex-shrink-0 bg-themed-success">
                <AlertCircle className="w-5 h-5 text-themed-success" />
              </div>
              <div>
                <h3 className="font-semibold mb-1 text-themed-primary">
                  {t('auth.features.themeCustomization.title')}
                </h3>
                <p className="text-sm text-themed-secondary">
                  {t('auth.features.themeCustomization.description')}
                </p>
              </div>
            </div>
          </div>
        </div>
      </Card>
    </div>
  );
};

export default AuthenticateTab;
