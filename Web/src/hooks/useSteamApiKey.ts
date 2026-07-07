import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useNotifications } from '@contexts/notifications';
import type { NotificationVariant } from '../types/operations';

interface UseSteamApiKeyOptions {
  initialApiKey?: string;
  onSaveSuccess?: () => void;
  /**
   * Surfaces the Test/Save lifecycle (validating/saving/valid/invalid/saved/failed) as one
   * universal-notification card, mirroring the Steam-login `generic` card on the Integrations
   * page. Opt-in because this hook is also used by the setup wizard, where the notification
   * bar is not part of the flow.
   */
  statusNotifications?: boolean;
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
  /**
   * Settles an abandoned Test/Save as a red, auto-dismissing cancel card. Call on modal
   * close/unmount so a card left `running` by a closed modal doesn't spin forever. No-op
   * when `statusNotifications` is false or no card is currently live.
   */
  cancelWebApiCard: () => void;
}

export function useSteamApiKey(options: UseSteamApiKeyOptions = {}): UseSteamApiKeyResult {
  const { initialApiKey = '', onSaveSuccess, statusNotifications = false } = options;
  const { t } = useTranslation();
  const { addNotification, updateNotification, scheduleAutoDismiss } = useNotifications();

  const [apiKey, setApiKey] = useState(initialApiKey);
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{ valid: boolean; message: string } | null>(null);

  // Id of the Web API key status card while a Test/Save this hook started is still live. Steam
  // Web API key actions are synchronous REST with no backend progress stream to share, so the
  // lifecycle lives in one 'generic' card updated in place; null once settled (mirrors
  // useSteamLoginFlow.loginCardIdRef).
  const webApiCardIdRef = useRef<string | null>(null);

  const upsertWebApiCard = (message: string): void => {
    if (!statusNotifications) {
      return;
    }
    if (webApiCardIdRef.current) {
      updateNotification(webApiCardIdRef.current, { status: 'running', message });
    } else {
      webApiCardIdRef.current = addNotification({
        type: 'generic',
        status: 'running',
        message,
        details: { notificationType: 'info' }
      });
    }
  };

  // status 'completed' + cancelled:true == RED + XCircle (cancel); 'failed' == RED;
  // plain 'completed' == green.
  const settleWebApiCard = (
    status: 'completed' | 'failed',
    message: string,
    variant: NotificationVariant,
    cancelled = false
  ): void => {
    if (!statusNotifications) {
      return;
    }
    const id = webApiCardIdRef.current;
    if (!id) {
      return;
    }
    webApiCardIdRef.current = null;
    updateNotification(id, { status, message, details: { notificationType: variant, cancelled } });
    scheduleAutoDismiss(id);
  };

  const cancelWebApiCard = (): void => {
    settleWebApiCard('completed', t('signalr.steamWebApi.cancelled'), 'warning', true);
  };

  const handleTest = async (emptyKeyMessage: string, networkErrorMessage: string) => {
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: emptyKeyMessage });
      return;
    }

    setTesting(true);
    setTestResult(null);
    upsertWebApiCard(t('signalr.steamWebApi.validating'));

    try {
      const data = await ApiService.testSteamApiKey(apiKey.trim());
      setTestResult({ valid: data.valid, message: data.message });
      if (data.valid) {
        settleWebApiCard('completed', t('signalr.steamWebApi.keyValid'), 'success');
      } else {
        settleWebApiCard(
          'failed',
          t('signalr.steamWebApi.keyInvalid', { errorDetail: data.message }),
          'error'
        );
      }
    } catch (error: unknown) {
      const message = getErrorMessage(error) || networkErrorMessage;
      setTestResult({ valid: false, message });
      settleWebApiCard(
        'failed',
        t('signalr.steamWebApi.keyInvalid', { errorDetail: message }),
        'error'
      );
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
    upsertWebApiCard(t('signalr.steamWebApi.saving'));

    try {
      await ApiService.saveSteamApiKey(apiKey.trim());
      settleWebApiCard('completed', t('signalr.steamWebApi.keySaved'), 'success');
      onSaveSuccess?.();
    } catch (error: unknown) {
      const message = getErrorMessage(error) || networkErrorMessage;
      setTestResult({ valid: false, message });
      settleWebApiCard(
        'failed',
        t('signalr.steamWebApi.keySaveFailed', { errorDetail: message }),
        'error'
      );
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
    resetTestResult,
    cancelWebApiCard
  };
}
