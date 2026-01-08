import React, { useState } from 'react';
import { Key, Lock, ExternalLink, CheckCircle, XCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import ApiService from '@services/api.service';

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
  const [apiKey, setApiKey] = useState('');
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{ valid: boolean; message: string } | null>(null);

  const handleTest = async () => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: 'Please enter an API key' });
      return;
    }

    setTesting(true);
    setTestResult(null);

    try {
      const response = await fetch('/api/steam-api-keys/test', {
        method: 'POST',
        headers: {
          ...ApiService.getHeaders(),
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ apiKey: apiKey.trim() })
      });

      const data = await response.json();

      if (response.ok) {
        setTestResult({
          valid: data.valid,
          message: data.message
        });
      } else {
        setTestResult({
          valid: false,
          message: data.error || 'Failed to test API key'
        });
      }
    } catch (error: unknown) {
      setTestResult({
        valid: false,
        message: (error instanceof Error ? error.message : String(error)) || 'Network error - failed to test API key'
      });
    } finally {
      setTesting(false);
    }
  };

  const handleSave = async () => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: 'Please enter an API key' });
      return;
    }

    setSaving(true);

    try {
      const response = await fetch('/api/steam-api-keys', {
        method: 'POST',
        headers: {
          ...ApiService.getHeaders(),
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ apiKey: apiKey.trim() })
      });

      const data = await response.json();

      if (response.ok) {
        onSuccess?.();
        onClose();
      } else {
        setTestResult({
          valid: false,
          message: data.error || data.message || 'Failed to save API key'
        });
      }
    } catch (error: unknown) {
      setTestResult({
        valid: false,
        message: (error instanceof Error ? error.message : String(error)) || 'Network error - failed to save API key'
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
          <span>Configure Steam Web API Key</span>
        </div>
      }
      size="lg"
    >
      <div className="space-y-4">
        {/* Info Section */}
        <div className="rounded-lg p-4 border bg-info border-info">
          <p className="text-sm text-themed-secondary mb-3">
            The Steam Web API V1 requires an API key for access. This is only needed if V2 becomes
            unavailable.
          </p>

          <div className="space-y-2 text-sm text-themed-secondary">
            <p className="font-medium text-info-text">
              Steps to get your API key:
            </p>
            <ol className="list-decimal list-inside space-y-1 ml-2">
              <li>
                Visit{' '}
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
              <li>Login with your Steam account</li>
              <li>
                Enter a domain name (can be anything):
                <ul className="list-disc list-inside ml-4 mt-1 space-y-0.5">
                  <li>Recommended: Your Steam username</li>
                  <li>Examples: "MyLancache", "HomeServer", etc.</li>
                  <li>This is just for organization - use any name you prefer</li>
                </ul>
              </li>
              <li>Copy your API key and paste below</li>
            </ol>
          </div>
        </div>

        {/* API Key Input */}
        <div>
          <label className="block text-sm font-medium text-themed-secondary mb-2">
            Steam Web API Key
          </label>
          <input
            type="password"
            value={apiKey}
            onChange={(e) => {
              setApiKey(e.target.value);
              setTestResult(null); // Clear test result when typing
            }}
            placeholder="Enter your Steam Web API key..."
            className="w-full px-4 py-2 rounded-lg themed-input"
          />
        </div>

        {/* Security Notice */}
        <div className="flex items-start gap-2 text-xs text-themed-muted">
          <Lock className="w-4 h-4 flex-shrink-0 mt-0.5" />
          <p>
            Your API key will be encrypted and stored securely alongside your Steam credentials
            using Microsoft Data Protection API.
          </p>
        </div>

        {/* Test Result */}
        {testResult && (
          <div
            className="rounded-lg p-3 border flex items-start gap-3"
            style={{
              backgroundColor: testResult.valid
                ? 'var(--theme-success-bg)'
                : 'var(--theme-error-bg)',
              borderColor: testResult.valid ? 'var(--theme-success)' : 'var(--theme-error)'
            }}
          >
            {testResult.valid ? (
              <CheckCircle
                className="w-5 h-5 flex-shrink-0 mt-0.5"
                style={{ color: 'var(--theme-success)' }}
              />
            ) : (
              <XCircle
                className="w-5 h-5 flex-shrink-0 mt-0.5"
                style={{ color: 'var(--theme-error)' }}
              />
            )}
            <div className="flex-1">
              <p
                className="text-sm font-medium"
                style={{
                  color: testResult.valid ? 'var(--theme-success-text)' : 'var(--theme-error-text)'
                }}
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
            {testing ? 'Testing...' : 'Test Connection'}
          </Button>

          <Button
            onClick={handleSave}
            variant="filled"
            color="blue"
            disabled={!apiKey.trim() || testing || saving || !!(testResult && !testResult.valid)}
            loading={saving}
            className="flex-1"
          >
            {saving ? 'Saving...' : 'Save API Key'}
          </Button>

          <Button onClick={handleClose} variant="default" disabled={testing || saving}>
            Cancel
          </Button>
        </div>
      </div>
    </Modal>
  );
};

export default SteamWebApiKeyModal;
