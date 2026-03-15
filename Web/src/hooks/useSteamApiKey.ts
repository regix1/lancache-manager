import { useState } from 'react';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';

interface UseSteamApiKeyOptions {
  initialApiKey?: string;
  onSaveSuccess?: () => void;
}

interface UseSteamApiKeyResult {
  apiKey: string;
  setApiKey: (key: string) => void;
  testing: boolean;
  saving: boolean;
  testResult: { valid: boolean; message: string } | null;
  handleTest: (emptyKeyMessage: string, networkErrorMessage: string) => Promise<void>;
  handleSave: (emptyKeyMessage: string, networkErrorMessage: string) => Promise<void>;
  resetTestResult: () => void;
}

export function useSteamApiKey(options: UseSteamApiKeyOptions = {}): UseSteamApiKeyResult {
  const { initialApiKey = '', onSaveSuccess } = options;

  const [apiKey, setApiKey] = useState(initialApiKey);
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{ valid: boolean; message: string } | null>(null);

  const handleTest = async (emptyKeyMessage: string, networkErrorMessage: string) => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: emptyKeyMessage });
      return;
    }

    setTesting(true);
    setTestResult(null);

    try {
      const data = await ApiService.testSteamApiKey(apiKey.trim());
      setTestResult({ valid: data.valid, message: data.message });
    } catch (error: unknown) {
      setTestResult({
        valid: false,
        message: getErrorMessage(error) || networkErrorMessage
      });
    } finally {
      setTesting(false);
    }
  };

  const handleSave = async (emptyKeyMessage: string, networkErrorMessage: string) => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: emptyKeyMessage });
      return;
    }

    setSaving(true);

    try {
      await ApiService.saveSteamApiKey(apiKey.trim());
      onSaveSuccess?.();
    } catch (error: unknown) {
      setTestResult({
        valid: false,
        message: getErrorMessage(error) || networkErrorMessage
      });
    } finally {
      setSaving(false);
    }
  };

  const resetTestResult = () => setTestResult(null);

  return {
    apiKey,
    setApiKey,
    testing,
    saving,
    testResult,
    handleTest,
    handleSave,
    resetTestResult
  };
}
