import type { NginxReopenGate } from './nginxReopenAvailability';

interface CardNoticeConditions {
  cacheWrite: boolean; // writes the cache dir  -> cache read-only (#1) + cache missing (#7)
  cacheRead: boolean; // reads the cache dir   -> cache missing (#7) only (read-only is fine)
  logsWrite: boolean; // rewrites the logs dir -> logs read-only (#2) + logs missing (#8)
  nginx: boolean; // rewrites logs and must reopen nginx -> nginx-reopen notice
}

interface CardNoticeLiveState {
  cacheReadOnly: boolean;
  logsReadOnly: boolean;
  cacheExist: boolean;
  logsExist: boolean;
  checkingPermissions: boolean;
  nginxReopenGate: NginxReopenGate;
}

export function isCardDiskActionBlocked(
  conditions: CardNoticeConditions,
  live: CardNoticeLiveState
): boolean {
  if (live.checkingPermissions) return false;
  if ((conditions.cacheWrite || conditions.cacheRead) && !live.cacheExist) return true;
  if (conditions.logsWrite && !live.logsExist) return true;
  if (conditions.cacheWrite && live.cacheReadOnly) return true;
  if (conditions.logsWrite && live.logsReadOnly) return true;
  return false;
}

export type CardNoticeColor = 'red' | 'orange';

export type CardNoticeBody =
  | { kind: 'ro'; prefixKey: string; suffixKey: string }
  | { kind: 'text'; key: string }
  | { kind: 'raw'; messageKey: string };

export interface CardNotice {
  color: CardNoticeColor;
  titleKey: string;
  body: CardNoticeBody;
}

export function resolveCardNotice(
  conditions: CardNoticeConditions,
  live: CardNoticeLiveState
): CardNotice | null {
  if (live.checkingPermissions) {
    return null;
  }

  const needCachePresent = conditions.cacheWrite || conditions.cacheRead;

  if (needCachePresent && !live.cacheExist) {
    return {
      color: 'red',
      titleKey: 'management.directoryNotice.cacheMissingTitle',
      body: { kind: 'text', key: 'management.directoryNotice.missingDescription' }
    };
  }

  if (conditions.logsWrite && !live.logsExist) {
    return {
      color: 'red',
      titleKey: 'management.directoryNotice.logsMissingTitle',
      body: { kind: 'text', key: 'management.directoryNotice.missingDescription' }
    };
  }

  if (conditions.cacheWrite && live.cacheReadOnly) {
    return {
      color: 'orange',
      titleKey: 'management.directoryNotice.cacheReadOnlyTitle',
      body: {
        kind: 'ro',
        prefixKey: 'management.directoryNotice.readOnlyPrefix',
        suffixKey: 'management.directoryNotice.readOnlySuffix'
      }
    };
  }

  if (conditions.logsWrite && live.logsReadOnly) {
    return {
      color: 'orange',
      titleKey: 'management.directoryNotice.logsReadOnlyTitle',
      body: {
        kind: 'ro',
        prefixKey: 'management.directoryNotice.readOnlyPrefix',
        suffixKey: 'management.directoryNotice.readOnlySuffix'
      }
    };
  }

  if (conditions.nginx && !live.nginxReopenGate.available && live.nginxReopenGate.messageKey) {
    return {
      color: 'orange',
      titleKey: 'management.nginxReopen.alertTitle',
      body: { kind: 'raw', messageKey: live.nginxReopenGate.messageKey }
    };
  }

  return null;
}
