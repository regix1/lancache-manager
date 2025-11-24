import React, { useState, useEffect } from 'react';
import { Key, Lock, ExternalLink, CheckCircle, XCircle, Loader2 } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import { storage } from '@utils/storage';

interface SteamApiKeyStepProps {
  onComplete: () => void;
}

export const SteamApiKeyStep: React.FC<SteamApiKeyStepProps> = ({ onComplete }) => {
  const [apiKey, setApiKey] = useState(() => {
    // Restore from localStorage
    return storage.getItem('steamApiKey') || '';
  });
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{ valid: boolean; message: string } | null>(null);

  // Persist apiKey to localStorage
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
    } catch (error: any) {
      setTestResult({
        valid: false,
        message: error.message || 'Network error - failed to test API key'
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
        // API key saved successfully, move to next step
        onComplete();
      } else {
        setTestResult({
          valid: false,
          message: data.error || data.message || 'Failed to save API key'
        });
      }
    } catch (error: any) {
      setTestResult({
        valid: false,
        message: error.message || 'Network error - failed to save API key'
      });
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <p className="text-themed-secondary text-center mb-6">
        To access Steam data, you need a Steam Web API v1 key. This allows Lancache Manager to
        fetch depot information from Steam.
      </p>

      {/* Info Section */}
      <div
        className="mb-6 rounded-lg p-4 border"
        style={{
          backgroundColor: 'var(--theme-info-bg)',
          borderColor: 'var(--theme-info)'
        }}
      >
        <div className="space-y-2 text-sm text-themed-secondary">
          <p className="font-medium" style={{ color: 'var(--theme-info-text)' }}>
            Steps to get your API key:
          </p>
          <ol className="list-decimal list-inside space-y-1 ml-2">
            <li>
              Visit{' '}
              <a
                href="https://steamcommunity.com/dev/apikey"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 font-medium hover:underline"
                style={{ color: 'var(--theme-info-text)' }}
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
      <div className="mb-4">
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
          className="w-full px-4 py-2 rounded-lg border themed-input"
          disabled={testing || saving}
        />
      </div>

      {/* Security Notice */}
      <div className="flex items-start gap-2 text-xs text-themed-muted mb-4">
        <Lock className="w-4 h-4 flex-shrink-0 mt-0.5" />
        <p>
          Your API key will be encrypted and stored securely alongside your Steam credentials using
          Microsoft Data Protection API.
        </p>
      </div>

      {/* Test Result */}
      {testResult && (
        <div
          className="mb-4 rounded-lg p-3 border flex items-start gap-3"
          style={{
            backgroundColor: testResult.valid ? 'var(--theme-success-bg)' : 'var(--theme-error-bg)',
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
      <div className="flex flex-col gap-3">
        <Button
          onClick={handleTest}
          variant="default"
          disabled={!apiKey.trim() || testing || saving}
          leftSection={testing ? <Loader2 className="w-4 h-4 animate-spin" /> : undefined}
          fullWidth
        >
          {testing ? 'Testing...' : 'Test Connection'}
        </Button>

        <Button
          onClick={handleSave}
          variant="filled"
          color="blue"
          leftSection={
            saving ? <Loader2 className="w-4 h-4 animate-spin" /> : <Key className="w-4 h-4" />
          }
          disabled={!apiKey.trim() || testing || saving || !!(testResult && !testResult.valid)}
          fullWidth
        >
          {saving ? 'Saving...' : 'Save and Continue'}
        </Button>
      </div>
    </>
  );
};
