import React, { useState } from 'react';
import { Key, Lock, ExternalLink, CheckCircle, XCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import ApiService from '@services/api.service';
import { useTranslation } from 'react-i18next';

interface SteamWebApiKeyModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

const SteamWebApiKeyModal: React.FC<SteamWebApiKeyModalProps> = ({
  isOpen,
  onClose,
  onSuccess
}) => {
  const { t } = useTranslation();
  const [apiKey, setApiKey] = useState('');
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{ valid: boolean; message: string } | null>(null);

  const handleTest = async () => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: t('modals.steamWebApi.errors.enterKey') });
      return;
    }

    setTesting(true);
    setTestResult(null);

    try {
      const response = await fetch('/api/steam-api-keys/test', ApiService.getFetchOptions({
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ apiKey: apiKey.trim() })
      }));

      const data = await response.json();

      if (response.ok) {
        setTestResult({
          valid: data.valid,
          message: data.message
        });
      } else {
        setTestResult({
          valid: false,
          message: data.error || t('modals.steamWebApi.errors.testFailed')
        });
      }
    } catch (error: unknown) {
      setTestResult({
        valid: false,
        message: (error instanceof Error ? error.message : String(error)) || t('modals.steamWebApi.errors.networkError')
      });
    } finally {
      setTesting(false);
    }
  };

  const handleSave = async () => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: t('modals.steamWebApi.errors.enterKey') });
      return;
    }

    setSaving(true);

    try {
      const response = await fetch('/api/steam-api-keys', ApiService.getFetchOptions({
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ apiKey: apiKey.trim() })
      }));

      const data = await response.json();

      if (response.ok) {
        onSuccess?.();
        onClose();
      } else {
        setTestResult({
          valid: false,
          message: data.error || data.message || t('modals.steamWebApi.errors.saveFailed')
        });
      }
    } catch (error: unknown) {
      setTestResult({
        valid: false,
        message: (error instanceof Error ? error.message : String(error)) || t('modals.steamWebApi.errors.networkErrorSave')
      });
    } finally {
      setSaving(false);
    }
  };

  const handleClose = () => {
    setApiKey('');
    setTestResult(null);
    onClose();
  };

  return (
    <Modal
      opened={isOpen}
      onClose={handleClose}
      title={
        <div className="flex items-center gap-3">
          <div className="p-2 rounded-lg bg-info">
            <Key className="w-6 h-6 text-info" />
          </div>
          <span>{t('modals.steamWebApi.title')}</span>
        </div>
      }
      size="lg"
    >
      <div className="space-y-4">
        {/* Info Section */}
        <div className="rounded-lg p-4 border bg-info border-info">
          <p className="text-sm text-themed-secondary mb-3">
            {t('modals.steamWebApi.info.description')}
          </p>

          <div className="space-y-2 text-sm text-themed-secondary">
            <p className="font-medium text-info-text">
              {t('modals.steamWebApi.info.stepsTitle')}
            </p>
            <ol className="list-decimal list-inside space-y-1 ml-2">
              <li>
                {t('modals.steamWebApi.info.step1')}{' '}
                <a
                  href="https://steamcommunity.com/dev/apikey"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex items-center gap-1 font-medium hover:underline text-info-text"
                >
                  steamcommunity.com/dev/apikey
                  <ExternalLink className="w-3 h-3" />
                </a>
              </li>
              <li>{t('modals.steamWebApi.info.step2')}</li>
              <li>
                {t('modals.steamWebApi.info.step3')}
                <ul className="list-disc list-inside ml-4 mt-1 space-y-0.5">
                  <li>{t('modals.steamWebApi.info.step3a')}</li>
                  <li>{t('modals.steamWebApi.info.step3b')}</li>
                  <li>{t('modals.steamWebApi.info.step3c')}</li>
                </ul>
              </li>
              <li>{t('modals.steamWebApi.info.step4')}</li>
            </ol>
          </div>
        </div>

        {/* API Key Input */}
        <div>
          <label className="block text-sm font-medium text-themed-secondary mb-2">
            {t('modals.steamWebApi.labels.apiKey')}
          </label>
          <input
            type="password"
            value={apiKey}
            onChange={(e) => {
              setApiKey(e.target.value);
              setTestResult(null); // Clear test result when typing
            }}
            placeholder={t('modals.steamWebApi.placeholder')}
            className="w-full px-4 py-2 rounded-lg themed-input"
          />
        </div>

        {/* Security Notice */}
        <div className="flex items-start gap-2 text-xs text-themed-muted">
          <Lock className="w-4 h-4 flex-shrink-0 mt-0.5" />
          <p>
            {t('modals.steamWebApi.security.description')}
          </p>
        </div>

        {/* Test Result */}
        {testResult && (
          <div
            className={`rounded-lg p-3 border flex items-start gap-3 ${
              testResult.valid
                ? 'bg-success border-success'
                : 'bg-error border-error'
            }`}
          >
            {testResult.valid ? (
              <CheckCircle className="w-5 h-5 flex-shrink-0 mt-0.5 text-success" />
            ) : (
              <XCircle className="w-5 h-5 flex-shrink-0 mt-0.5 text-error" />
            )}
            <div className="flex-1">
              <p
                className={`text-sm font-medium ${
                  testResult.valid ? 'text-success-text' : 'text-error-text'
                }`}
              >
                {testResult.message}
              </p>
            </div>
          </div>
        )}

        {/* Action Buttons */}
        <div className="flex flex-col sm:flex-row gap-3 pt-2">
          <Button
            onClick={handleTest}
            variant="default"
            disabled={!apiKey.trim() || testing || saving}
            loading={testing}
            className="flex-1"
          >
            {testing ? t('modals.steamWebApi.actions.testing') : t('modals.steamWebApi.actions.testConnection')}
          </Button>

          <Button
            onClick={handleSave}
            variant="filled"
            color="blue"
            disabled={!apiKey.trim() || testing || saving || !!(testResult && !testResult.valid)}
            loading={saving}
            className="flex-1"
          >
            {saving ? t('modals.steamWebApi.actions.saving') : t('modals.steamWebApi.actions.saveApiKey')}
          </Button>

          <Button onClick={handleClose} variant="default" disabled={testing || saving}>
            {t('common.cancel')}
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default SteamWebApiKeyModal;
