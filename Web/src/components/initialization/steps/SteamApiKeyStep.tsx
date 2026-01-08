import React, { useState, useEffect } from 'react';
import { Key, ExternalLink, CheckCircle, XCircle, Loader2, Shield } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import { storage } from '@utils/storage';

interface SteamApiKeyStepProps {
  onComplete: () => void;
}

export const SteamApiKeyStep: React.FC<SteamApiKeyStepProps> = ({ onComplete }) => {
  const [apiKey, setApiKey] = useState(() => {
    return storage.getItem('steamApiKey') || '';
  });
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{ valid: boolean; message: string } | null>(null);

  useEffect(() => {
    if (apiKey) {
      storage.setItem('steamApiKey', apiKey);
    } else {
      storage.removeItem('steamApiKey');
    }
  }, [apiKey]);

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
        onComplete();
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

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-primary-subtle">
          <Key className="w-7 h-7 icon-primary" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">Steam Web API Key</h3>
        <p className="text-sm text-themed-secondary max-w-md">
          Required to fetch depot information directly from Steam
        </p>
      </div>

      {/* Instructions */}
      <div className="p-4 rounded-lg bg-themed-tertiary">
        <p className="text-sm font-medium text-themed-primary mb-2">How to get your API key:</p>
        <ol className="text-sm text-themed-secondary space-y-1.5 list-decimal list-inside">
          <li>
            Visit{' '}
            <a
              href="https://steamcommunity.com/dev/apikey"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 text-themed-accent hover:underline"
            >
              steamcommunity.com/dev/apikey
              <ExternalLink className="w-3 h-3" />
            </a>
          </li>
          <li>Login with your Steam account</li>
          <li>Enter any domain name (e.g., "MyLancache")</li>
          <li>Copy and paste your key below</li>
        </ol>
      </div>

      {/* API Key Input */}
      <div>
        <label className="block text-sm font-medium text-themed-secondary mb-1.5">
          API Key
        </label>
        <input
          type="password"
          value={apiKey}
          onChange={(e) => {
            setApiKey(e.target.value);
            setTestResult(null);
          }}
          placeholder="Enter your Steam Web API key"
          className="w-full px-3 py-2.5 themed-input"
          disabled={testing || saving}
        />
      </div>

      {/* Test Result */}
      {testResult && (
        <div
          className={`p-3 rounded-lg flex items-start gap-3 ${
            testResult.valid ? 'bg-themed-success' : 'bg-themed-error'
          }`}
        >
          {testResult.valid ? (
            <CheckCircle className="w-5 h-5 flex-shrink-0 icon-success" />
          ) : (
            <XCircle className="w-5 h-5 flex-shrink-0 icon-error" />
          )}
          <p className={`text-sm ${testResult.valid ? 'text-themed-success' : 'text-themed-error'}`}>
            {testResult.message}
          </p>
        </div>
      )}

      {/* Security Note */}
      <div className="flex items-start gap-2 text-xs text-themed-muted">
        <Shield className="w-4 h-4 flex-shrink-0 mt-0.5 icon-success" />
        <p>Your API key is encrypted and stored securely using Microsoft Data Protection API.</p>
      </div>

      {/* Action Buttons */}
      <div className="flex gap-3 pt-2">
        <Button
          variant="default"
          onClick={handleTest}
          disabled={!apiKey.trim() || testing || saving}
          className="flex-1"
        >
          {testing && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
          {testing ? 'Testing...' : 'Test Connection'}
        </Button>

        <Button
          variant="filled"
          color="green"
          onClick={handleSave}
          disabled={!apiKey.trim() || testing || saving || !testResult?.valid}
          className="flex-1"
        >
          {saving && <Loader2 className="w-4 h-4 animate-spin mr-2" />}
          {saving ? 'Saving...' : 'Save & Continue'}
        </Button>
      </div>
    </div>
  );
};
