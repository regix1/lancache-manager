import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { isAbortError } from '@utils/error';

/** What the server has said so far about a scan starting right now. */
type CacheScanAnswer = 'checking' | 'blocked' | 'clear';

interface CacheScanGate {
  /** True only once the server has answered that nothing is writing to the cache. */
  available: boolean;
  /** True only once the server has answered that a scan would be refused right now. */
  blocked: boolean;
  /**
   * Hover explanation while the action is not offered: the server's own sentence once it has
   * answered, this hook's own while the answer is outstanding. Empty string once available.
   */
  tooltip: string;
}

/**
 * Whether a cache scan would be refused right now, asked of the server rather than derived from
 * the speed snapshot. The snapshot is the client-visible projection and drops hidden clients, but
 * a hidden client's bytes still reach the cache, so gating on it left the buttons enabled during
 * their download. This reads the same gate the server refuses with, so the two agree.
 *
 * There are three answers, not two, and the third is "not yet". Between first paint and the first
 * response the gate is closed but says only that it is checking: a scan control that looks ready
 * before anything has been asked is a claim the client cannot support, and the download sentence
 * would be a different claim it cannot support either. `available` and `blocked` are therefore not
 * negations of each other - both are false while the answer is outstanding.
 *
 * A read that fails opens the gate. The server holds the authoritative gate and refuses the click
 * with its own sentence, so an unreadable answer costs at most a click, while leaving the controls
 * shut would lock scanning behind a reason that was never established.
 *
 * Blocked has two causes and the server says which: a client download is writing, or the download
 * tracker has not reported yet so nothing can tell. Both arrive as `blocked` with a different
 * sentence, and only the server can tell them apart, so its sentence is what gets shown rather
 * than a client string that would state a download on both.
 */
export function useCacheScanBlocked(): CacheScanGate {
  const { t } = useTranslation();
  const { on, off, isConnected } = useSignalR();
  const [answer, setAnswer] = useState<CacheScanAnswer>('checking');
  const [blockedReason, setBlockedReason] = useState('');

  const refresh = useCallback(async () => {
    try {
      const result = await ApiService.getCacheScanBlocked();
      setAnswer(result.blocked ? 'blocked' : 'clear');
      setBlockedReason(result.reason ?? '');
    } catch (err: unknown) {
      // A failed read must not claim a download is running: the server still refuses on click.
      if (!isAbortError(err)) {
        setAnswer('clear');
      }
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    const handleChanged = () => {
      void refresh();
    };
    on('CacheScanBlockedChanged', handleChanged);
    return () => {
      off('CacheScanBlockedChanged', handleChanged);
    };
  }, [on, off, refresh]);

  // The event carries no payload, so one missed while the socket was down leaves this stale.
  useReconnectRefetch(isConnected, () => {
    void refresh();
  });

  let tooltip = '';
  if (answer === 'blocked') {
    tooltip = blockedReason;
  } else if (answer === 'checking') {
    tooltip = t('management.gameDetection.checkingForDownload');
  }

  return { available: answer === 'clear', blocked: answer === 'blocked', tooltip };
}
