import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/useAuth';
import { useSteamWebApiStatus } from '@contexts/useSteamWebApiStatus';
import { getIntegrationReasonKey } from '../types';
import { useNotifications } from '@contexts/notifications';
import type { NotificationVariant } from '../types/operations';

/**
 * The key is write-only. There is deliberately no way to seed the field with an existing key: the
 * server never returns one (the status endpoint answers with a boolean), and nothing keeps a copy
 * on the client, so a saved key cannot be read back by anyone, including whoever entered it.
 */
interface UseSteamApiKeyOptions {
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
  canManage: boolean;
  ownershipReason: string | null;
  apiKey: string;
  setApiKey: (key: string) => void;
  testing: boolean;
  saving: boolean;
  /**
   * `title` is the failed action's first line, set only when the request itself failed; a key
   * verdict and the empty-key message have none.
   */
  testResult: { valid: boolean; message: string; title?: string } | null;
  handleTest: (emptyKeyMessage: string) => Promise<void>;
  handleSave: (emptyKeyMessage: string) => Promise<void>;
  resetTestResult: () => void;
  /**
   * Settles an abandoned Test/Save as a red, auto-dismissing cancel card. Call on modal
   * close/unmount so a card left `running` by a closed modal doesn't spin forever. No-op
   * when `statusNotifications` is false or no card is currently live.
   */
  cancelWebApiCard: () => void;
}

export function useSteamApiKey(options: UseSteamApiKeyOptions = {}): UseSteamApiKeyResult {
  const { onSaveSuccess, statusNotifications = false } = options;
  const { t } = useTranslation();
  const { authenticationEnabled, authMode, accountId, sessionId, isLoading } = useAuth();
  const { status, error, refresh } = useSteamWebApiStatus();
  const identity = JSON.stringify([authenticationEnabled, authMode, accountId, sessionId]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const formIdentityRef = useRef(identity);
  const requestRef = useRef(0);
  const busyRef = useRef(false);
  const canManage =
    !isLoading &&
    formIdentityRef.current === identity &&
    (authenticationEnabled === false || (error === null && status?.canManage === true));
  const ownershipReason = canManage
    ? null
    : !isLoading &&
        formIdentityRef.current === identity &&
        error === null &&
        status?.canManage === false
      ? t(getIntegrationReasonKey(status.ownershipReason))
      : t('errors.integration.statusUnavailable');
  const { addNotification, updateNotification, scheduleAutoDismiss } = useNotifications();

  const [apiKey, setApiKey] = useState('');
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testResult, setTestResult] = useState<{
    valid: boolean;
    message: string;
    title?: string;
  } | null>(null);

  // Id of the Web API key status card while a Test/Save this hook started is still live. Steam
  // Web API key actions are synchronous REST with no backend progress stream to share, so the
  // lifecycle lives in one 'generic' card updated in place; null once settled.
  const webApiCardIdRef = useRef<string | null>(null);

  useEffect(() => {
    formIdentityRef.current = identity;
    requestRef.current += 1;
    busyRef.current = false;
    webApiCardIdRef.current = null;
    setApiKey('');
    setTesting(false);
    setSaving(false);
    setTestResult(null);
    return () => {
      requestRef.current += 1;
    };
  }, [identity]);

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
        details: { serviceKey: 'depotMapping' }
      });
    }
  };

  // status 'completed' + cancelled:true == RED + XCircle (cancel); 'failed' == RED;
  // plain 'completed' == green.
  const settleWebApiCard = (
    status: 'completed' | 'failed',
    message: string,
    variant: NotificationVariant,
    cancelled = false,
    error?: string
  ): void => {
    if (!statusNotifications) {
      return;
    }
    const id = webApiCardIdRef.current;
    if (!id) {
      return;
    }
    webApiCardIdRef.current = null;
    updateNotification(id, {
      status,
      message,
      error,
      details: { notificationType: variant, cancelled, serviceKey: 'depotMapping' }
    });
    scheduleAutoDismiss(id);
  };

  const cancelWebApiCard = (): void => {
    if (identityRef.current !== identity) return;
    requestRef.current += 1;
    busyRef.current = false;
    setTesting(false);
    setSaving(false);
    settleWebApiCard('completed', t('signalr.steamWebApi.cancelled'), 'warning', true);
  };

  const handleTest = async (emptyKeyMessage: string) => {
    if (identityRef.current !== identity || !canManage || busyRef.current) return;
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: emptyKeyMessage });
      return;
    }

    setTesting(true);
    busyRef.current = true;
    const request = ++requestRef.current;
    const current = () => identityRef.current === identity && requestRef.current === request;
    setTestResult(null);
    upsertWebApiCard(t('signalr.steamWebApi.validating'));

    try {
      const data = await ApiService.testSteamApiKey(apiKey.trim());
      if (!current()) return;
      const verdict = data.valid
        ? t('management.steamWebApi.test.valid')
        : t('management.steamWebApi.test.invalid');
      setTestResult({ valid: data.valid, message: verdict });
      if (data.valid) {
        settleWebApiCard('completed', t('signalr.steamWebApi.keyValid'), 'success');
      } else {
        settleWebApiCard('failed', t('management.steamWebApi.test.invalid'), 'error');
      }
    } catch (error: unknown) {
      if (!current()) return;
      const message = getErrorMessage(error);
      setTestResult({ valid: false, title: t('signalr.steamWebApi.testFailed'), message });
      settleWebApiCard('failed', t('signalr.steamWebApi.testFailed'), 'error', false, message);
    } finally {
      if (current()) {
        busyRef.current = false;
        setTesting(false);
      }
    }
  };

  const handleSave = async (emptyKeyMessage: string) => {
    if (identityRef.current !== identity || !canManage || busyRef.current) return;
    if (!apiKey.trim()) {
      setTestResult({ valid: false, message: emptyKeyMessage });
      return;
    }

    setSaving(true);
    busyRef.current = true;
    const request = ++requestRef.current;
    const current = () => identityRef.current === identity && requestRef.current === request;
    upsertWebApiCard(t('signalr.steamWebApi.saving'));

    try {
      await ApiService.saveSteamApiKey(apiKey.trim());
      if (!current()) return;
      settleWebApiCard('completed', t('signalr.steamWebApi.keySaved'), 'success');
      onSaveSuccess?.();
    } catch (error: unknown) {
      if (!current()) return;
      const message = getErrorMessage(error);
      setTestResult({ valid: false, title: t('signalr.steamWebApi.keySaveFailed'), message });
      settleWebApiCard('failed', t('signalr.steamWebApi.keySaveFailed'), 'error', false, message);
    } finally {
      if (current()) {
        busyRef.current = false;
        setSaving(false);
        void refresh();
      }
    }
  };

  const resetTestResult = () => setTestResult(null);

  return {
    canManage,
    ownershipReason,
    apiKey: formIdentityRef.current === identity ? apiKey : '',
    setApiKey,
    testing: formIdentityRef.current === identity && testing,
    saving: formIdentityRef.current === identity && saving,
    testResult: formIdentityRef.current === identity ? testResult : null,
    handleTest,
    handleSave,
    resetTestResult,
    cancelWebApiCard
  };
}
