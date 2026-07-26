import { useEffect } from 'react';
import ApiService from '@services/api.service';
import {
  beginEditSessionCleanup,
  buildEditSessionCleanupRequest,
  clearConfirmedEditSession,
  createScheduledPrefillEditSessionId,
  hasScheduledPrefillEditActions,
  loadScheduledPrefillEditSession
} from './scheduledPrefillEditSessionLedger';

const CLEANUP_RETRY_DELAY_MS = 5000;

/**
 * App-lifetime recovery for edit-session cleanup that survived a page unload or transient request
 * failure. This is deliberately mounted outside the management tab: the default dashboard route
 * must retry sessionStorage compensation without requiring the user to reopen the modal.
 */
export function ScheduledPrefillEditSessionCleanupRecovery() {
  useEffect(() => {
    let disposed = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;

    const retry = async () => {
      const stored = loadScheduledPrefillEditSession(sessionStorage);
      if (!stored || !hasScheduledPrefillEditActions(stored)) {
        return;
      }

      const pending = beginEditSessionCleanup(
        sessionStorage,
        stored,
        createScheduledPrefillEditSessionId
      );
      try {
        await ApiService.cleanupPersistentPrefillEditSession(
          buildEditSessionCleanupRequest(pending)
        );
        clearConfirmedEditSession(sessionStorage, pending.editSessionId, pending.cleanupId!);
      } catch {
        if (!disposed) {
          retryTimer = setTimeout(() => {
            void retry();
          }, CLEANUP_RETRY_DELAY_MS);
        }
      }
    };

    void retry();
    return () => {
      disposed = true;
      if (retryTimer !== null) {
        clearTimeout(retryTimer);
      }
    };
  }, []);

  return null;
}
