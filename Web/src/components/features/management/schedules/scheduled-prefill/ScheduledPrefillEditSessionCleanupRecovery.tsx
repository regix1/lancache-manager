import { useEffect } from 'react';
import ApiService from '@services/api.service';
import { recoverScheduledPrefillEditSession } from './scheduledPrefillEditSessionLedger';
import { sessionStore } from '@utils/storage';

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
      try {
        await recoverScheduledPrefillEditSession(sessionStore, (request) =>
          ApiService.cleanupPersistentPrefillEditSession(request)
        );
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
