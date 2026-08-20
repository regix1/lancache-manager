import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Users,
  Trash2,
  Network,
  Edit,
  Lock,
  Unlock,
  ChevronDown,
  Download,
  History,
  RotateCcw,
  Eraser,
  Search
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Modal } from '@components/ui/Modal';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { Tooltip } from '@components/ui/Tooltip';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { Pagination } from '@components/ui/Pagination';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { AccordionSection } from '@components/ui/AccordionSection';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionHeaderActions, SectionHeaderChip } from '@components/ui/SectionHeaderActions';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import StatusDot from '@components/common/StatusDot';
import { ActionMenuItem, ActionMenuDivider, ActionMenuDangerItem } from '@components/ui/ActionMenu';
import { EmptyState, LoadingState } from '@components/ui/ManagerCard';
import '../management/managementSectionContent.css';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import authService, { isAccountHolder } from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { FormattedTimestamp } from '@components/common/FormattedDateTime';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import type {
  EpicGuestPrefillConfigChangedEvent,
  XboxGuestPrefillConfigChangedEvent
} from '@contexts/SignalRContext/types';
import {
  PREFILL_SERVICES,
  prefillServiceConfig,
  prefillServiceRecord,
  type PrefillServiceConfig
} from '@components/features/prefill/hooks/prefillServiceConfig';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { TIME_SETTING_VALUES } from '@contexts/TimezoneContext.types';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useUserPresence } from '@contexts/UserPresenceContext/useUserPresence';
import { storage } from '@utils/storage';
import { parseUtcDate } from '@utils/timezone';
import type { UserPreferences } from '@/types/userPreferences';
import type { GameServiceId } from '@/types/gameService';
import {
  type Session,
  type SessionFilter,
  type ThemeOption,
  refreshRateOptions,
  cleanIpAddress,
  countryCodeToFlag,
  formatLocation,
  showToast,
  parseUserAgent
} from './types';
import { getTimeFormatOptions, guestTimeFormatKeys } from './timeFormatOptions';
import { getThreadOptions } from './threadOptions';

// ============================================================
// Local storage / page-size helpers (not exported — Fast Refresh)
// ============================================================

const STORAGE_KEYS = {
  PAGE_SIZE: 'lancache_active_sessions_page_size'
} as const;

const PAGE_SIZE_OPTIONS = [5, 10, 15, 20] as const;
type SessionPageSize = (typeof PAGE_SIZE_OPTIONS)[number];
const DEFAULT_PAGE_SIZE: SessionPageSize = 5;

const isSessionPageSize = (value: number): value is SessionPageSize =>
  (PAGE_SIZE_OPTIONS as readonly number[]).includes(value);

// ============================================================
// Prefill thread limits (not exported - Fast Refresh)
// ============================================================

// Steam and Epic each enforce their own per-session thread limit, so the editor
// renders one dropdown per service rather than a single shared control.
type ThreadLimitKey = 'steamMaxThreadCount' | 'epicMaxThreadCount';

interface ThreadLimitService {
  key: ThreadLimitKey;
  // Service brand names read the same in every locale, so they are not translated.
  name: string;
  defaultThreadCount: number | null;
}

// ============================================================
// Pending prefill grants (not exported - Fast Refresh)
// ============================================================

// Unsaved grant/revoke toggles in the session editor. Distinct from the thread limits
// above: those are saved preferences on the session, these are queued permission changes
// posted one call per service when the editor is saved.
type PendingPrefillChanges = Record<GameServiceId, boolean | null>;

const NO_PENDING_PREFILL_CHANGES: PendingPrefillChanges = prefillServiceRecord<boolean | null>(
  () => null
);

// ============================================================
// Presence thresholds (not exported - Fast Refresh)
// ============================================================

// How old lastSeenAt may be before a session stops reading as "active". This MUST stay comfortably
// larger than the rate at which the server actually refreshes lastSeenAt, or a session whose user is
// working right now will still age past it between two consecutive writes.
//
// The server throttles the write to once per 60s (SessionService.UpdateLastSeenAsync) and the browser
// heartbeat runs on a 30s timer (useActivityTracker), so a write lands on the first heartbeat at or
// after the throttle expires - up to 60 + 30 seconds after the previous one, and the two timers drift
// apart rather than staying phase-locked, since goActive() also fires heartbeats off-cadence. Add the
// browser-vs-server clock skew that "now - lastSeenAt" is computed across and a 60s window is
// guaranteed to lapse mid-cycle, which read as the presence dot flickering active/away every minute.
// Ambient API traffic used to hide this by refreshing lastSeenAt constantly; presence is now
// deliberately driven by the heartbeat alone, so the window has to cover the heartbeat's own period.
const ACTIVE_MAX_AGE_SECONDS = 60 + 30 + 30;
// Beyond this the session reads as fully inactive rather than merely away.
const AWAY_MAX_AGE_SECONDS = 600;

const loadPageSize = (): SessionPageSize => {
  const saved = storage.getItem(STORAGE_KEYS.PAGE_SIZE);
  if (saved === null) return DEFAULT_PAGE_SIZE;
  const parsed = Number.parseInt(saved, 10);
  return Number.isFinite(parsed) && isSessionPageSize(parsed) ? parsed : DEFAULT_PAGE_SIZE;
};

const sessionMatchesSearch = (session: Session, query: string): boolean => {
  const normalized = query.trim().toLowerCase();
  if (!normalized) return true;

  const parsedUA = parseUserAgent(session.userAgent);
  const ip = session.ipAddress ? cleanIpAddress(session.ipAddress) : '';
  const haystack = [
    parsedUA.title,
    parsedUA.browser,
    parsedUA.os,
    session.userAgent ?? '',
    session.ipAddress ?? '',
    ip,
    session.publicIpAddress ?? ''
  ]
    .join(' ')
    .toLowerCase();

  return haystack.includes(normalized);
};

// ============================================================
// Props Interface
// ============================================================

interface ActiveSessionsProps {
  guestDurationHours: number;
  guestModeLocked: boolean;
  updatingGuestLock: boolean;
  onToggleGuestLock: (value?: string) => Promise<void>;
  availableThemes: ThemeOption[];
  defaultGuestTheme: string;
  defaultGuestRefreshRate: string;
  sessions: Session[];
  setSessions: React.Dispatch<React.SetStateAction<Session[]>>;
  loading: boolean;
  setLoading: React.Dispatch<React.SetStateAction<boolean>>;
  onSessionsChange: () => void;
  refreshKey?: number;
  activeFilter?: SessionFilter;
  onFilterChange?: (filter: SessionFilter) => void;
}

// ============================================================
// Helper Components
// ============================================================

// ============================================================
// Pure Helper Functions
// ============================================================

// An account session, whichever role it holds. A user signs in against an account and gets the same
// access an admin does, so it belongs in the same count, the same filter and the same badge; only a
// guest is listed apart.
const isAdminSession = (session: Session): boolean => {
  return isAccountHolder(session.sessionType ?? null);
};

const isGuestSession = (session: Session): boolean => {
  return session.sessionType === 'guest';
};

// ============================================================
// Main Component
// ============================================================

const ActiveSessions: React.FC<ActiveSessionsProps> = ({
  guestDurationHours,
  guestModeLocked,
  updatingGuestLock,
  onToggleGuestLock,
  availableThemes,
  defaultGuestTheme,
  defaultGuestRefreshRate,
  sessions,
  setSessions,
  loading,
  setLoading,
  onSessionsChange,
  refreshKey,
  activeFilter: controlledFilter,
  onFilterChange
}) => {
  const { t } = useTranslation();
  const { refreshAuth } = useAuth();
  const { notifyError } = useErrorHandler();
  const { on, off, isConnected } = useSignalR();
  // Session presence flows through the unified activity registry; the last-seen aging below still
  // decides active vs away, so away/inactive is never collapsed into a boolean.
  const activity = useActivityStatus();
  const { prefs: defaultGuestPrefs } = useDefaultGuestPreferences();

  const {
    getSessionPreferences,
    loadSessionPreferences,
    isLoaded: isPreferencesLoaded,
    isLoading: isPreferencesLoading
  } = useSessionPreferences();

  // ============================================================
  // State
  // ============================================================

  // Filter state - support both controlled and uncontrolled
  const [localFilter, setLocalFilter] = useState<SessionFilter>('all');
  const activeFilterValue = controlledFilter ?? localFilter;
  const setActiveFilter = (filter: SessionFilter) => {
    if (onFilterChange) {
      onFilterChange(filter);
    } else {
      setLocalFilter(filter);
    }
  };

  // Section expand state (primary open by default; no localStorage)
  const [sessionsExpanded, setSessionsExpanded] = useState(true);
  useAccordionGroupItem('sessions-active', sessionsExpanded, () =>
    setSessionsExpanded((prev) => !prev)
  );

  // Bulk actions state
  const [showBulkResetConfirm, setShowBulkResetConfirm] = useState(false);
  const [showClearGuestsConfirm, setShowClearGuestsConfirm] = useState(false);
  const [bulkActionInProgress, setBulkActionInProgress] = useState<string | null>(null);

  // Session actions state
  const [revokingSession, setRevokingSession] = useState<string | null>(null);
  const [expandedSessions, setExpandedSessions] = useState<Set<string>>(new Set());
  // Read the app-wide presence tracker rather than mounting one here: a tracker owned by this page
  // would stop heartbeating the moment the user navigated away (see UserPresenceProvider).
  const { isActive: isLocallyActive } = useUserPresence();
  // Periodic tick so getSessionStatus() recomputes as lastSeenAt ages.
  // Without this, the status "sticks" between render-triggering events and
  // flips abruptly when some unrelated re-render happens.
  const [, setStatusTick] = useState<number>(0);
  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [loggingOut, setLoggingOut] = useState(false);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);

  // Edit modal state
  const [editingSession, setEditingSession] = useState<Session | null>(null);
  const [editingPreferences, setEditingPreferences] = useState<UserPreferences | null>(null);
  // Unsaved grant/revoke per service, keyed by service id. null means "no change pending",
  // which is what distinguishes an untouched row from one toggled back to its saved value.
  const [pendingPrefillChanges, setPendingPrefillChanges] = useState<PendingPrefillChanges>(
    NO_PENDING_PREFILL_CHANGES
  );
  const [loadingPreferences, setLoadingPreferences] = useState(false);
  const [savingPreferences, setSavingPreferences] = useState(false);

  // Text search (not persisted) + pagination over the filtered group.
  // Pages are computed client-side over type + text filters so each filter
  // paginates its own sessions instead of the server's all-sessions page count.
  const [searchQuery, setSearchQuery] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize, setPageSize] = useState<SessionPageSize>(loadPageSize);

  // History state
  const [historyExpanded, setHistoryExpanded] = useState(false);
  useAccordionGroupItem('sessions-history', historyExpanded, () =>
    setHistoryExpanded((prev) => !prev)
  );
  const [historySessions, setHistorySessions] = useState<Session[]>([]);

  // Thread config state
  const [defaultGuestMaxThreadCount, setDefaultGuestMaxThreadCount] = useState<number | null>(null);
  const [epicDefaultGuestMaxThreadCount, setEpicDefaultGuestMaxThreadCount] = useState<
    number | null
  >(null);

  // Dropdown options
  const timeFormatOptions = getTimeFormatOptions(t, guestTimeFormatKeys);

  const translatedRefreshRateOptions = refreshRateOptions.map((option) => ({
    ...option,
    label: t(`user.guest.refreshRates.${option.value}`)
  }));

  // ============================================================
  // API Functions
  // ============================================================

  const loadSessions = useCallback(
    async (showLoading = false) => {
      try {
        if (showLoading) {
          setLoading(true);
        }
        // The server pages over ALL active sessions (pageSize capped at 100) and
        // knows nothing about the admin/guest filter, so load every active
        // session (following server pages when needed) and paginate client-side
        // per filtered group. History rides along on the first response only.
        interface SessionsResponse {
          sessions: Session[];
          pagination: { totalPages: number; totalCount: number; page: number };
          historySessions: Session[];
        }
        const first = await ApiService.getSessions<SessionsResponse>(1, 100);
        // A response missing either array still resolves, so it never reaches the catch below.
        // Falling back to an empty list keeps the page on its empty state instead of throwing
        // when the counts below read .length.
        let loadedSessions = first.sessions ?? [];
        const serverPages = first.pagination?.totalPages || 1;
        if (serverPages > 1) {
          const rest = await Promise.all(
            Array.from({ length: serverPages - 1 }, (_: unknown, i: number) =>
              ApiService.getSessions<SessionsResponse>(i + 2, 100)
            )
          );
          loadedSessions = loadedSessions.concat(
            rest.flatMap((r: SessionsResponse) => r.sessions ?? [])
          );
        }
        setSessions(loadedSessions);
        setHistorySessions(first.historySessions ?? []);
      } catch (err: unknown) {
        notifyError(t('activeSessions.errors.loadSessions'), err, {
          logLabel: 'Failed to load sessions'
        });
      } finally {
        setLoading(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [setLoading, setSessions]
  );

  // Restart at page 1 when type filter, text search, or page size changes so a
  // deep page can't strand the user on an empty slice.
  useEffect(() => {
    setCurrentPage(1);
  }, [activeFilterValue, searchQuery, pageSize]);

  const handleSearchChange = (event: React.ChangeEvent<HTMLInputElement>): void => {
    setSearchQuery(event.target.value);
  };

  const handleClearSearch = (): void => {
    setSearchQuery('');
  };

  const handlePageSizeChange = (value: string): void => {
    const parsed = Number.parseInt(value, 10);
    if (!isSessionPageSize(parsed)) return;
    setPageSize(parsed);
    storage.setItem(STORAGE_KEYS.PAGE_SIZE, String(parsed));
  };

  const confirmRevokeSession = async () => {
    if (!pendingRevokeSession) return;

    const isOwnSession = pendingRevokeSession.isCurrentSession;

    try {
      setRevokingSession(pendingRevokeSession.id);
      await ApiService.revokeSession(pendingRevokeSession.id);

      if (isOwnSession) {
        setPendingRevokeSession(null);
        showToast('info', t('activeSessions.info.revokedOwnSession'));

        setTimeout(async () => {
          await authService.logout();
          await refreshAuth();
        }, 2000);
        return;
      }

      await loadSessions(false);
      setPendingRevokeSession(null);
      onSessionsChange();
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.revokeSession'), err, {
        logLabel: 'Failed to revoke session'
      });
    } finally {
      setRevokingSession(null);
    }
  };

  const confirmDeleteSession = async () => {
    if (!pendingDeleteSession) return;

    const isOwnSession = pendingDeleteSession.isCurrentSession;

    try {
      setDeletingSession(pendingDeleteSession.id);
      await ApiService.deleteSession(pendingDeleteSession.id);

      setSessions((prev) => prev.filter((s: Session) => s.id !== pendingDeleteSession.id));
      setPendingDeleteSession(null);
      onSessionsChange();

      if (isOwnSession) {
        showToast('info', t('activeSessions.info.deletedOwnSession'));
        setTimeout(async () => {
          await authService.logout();
          await refreshAuth();
        }, 2000);
      }
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.deleteSession'), err, {
        logLabel: 'Failed to delete session'
      });
    } finally {
      setDeletingSession(null);
    }
  };

  const handleLogout = async () => {
    try {
      setLoggingOut(true);
      await authService.logout();
      await refreshAuth();
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.logout'), err, { logLabel: 'Failed to log out' });
    } finally {
      setLoggingOut(false);
    }
  };

  const handleEditSession = async (session: Session) => {
    setEditingSession(session);
    setPendingPrefillChanges(NO_PENDING_PREFILL_CHANGES);
    setLoadingPreferences(true);
    try {
      const prefs = await ApiService.getSessionPreferences<UserPreferences>(session.id);
      const selectedTheme =
        typeof prefs.selectedTheme === 'string' && prefs.selectedTheme.trim() !== ''
          ? prefs.selectedTheme
          : null;
      setEditingPreferences({
        selectedTheme: selectedTheme,
        sharpCorners: prefs.sharpCorners,
        disableFocusOutlines: prefs.disableFocusOutlines,
        disableTooltips: prefs.disableTooltips,
        picsAlwaysVisible: prefs.picsAlwaysVisible,
        disableStickyNotifications: prefs.disableStickyNotifications,
        showDatasourceLabels: prefs.showDatasourceLabels,
        useLocalTimezone: prefs.useLocalTimezone,
        useUtcTimezone: prefs.useUtcTimezone,
        use24HourFormat: prefs.use24HourFormat,
        refreshRate: prefs.refreshRate ?? null,
        refreshRateLocked: prefs.refreshRateLocked ?? null,
        allowedTimeFormats: prefs.allowedTimeFormats,
        steamMaxThreadCount: prefs.steamMaxThreadCount ?? null,
        epicMaxThreadCount: prefs.epicMaxThreadCount ?? null
      });
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.loadPreferences'), err, {
        logLabel: 'Failed to load session preferences'
      });
      setEditingSession(null);
    } finally {
      setLoadingPreferences(false);
    }
  };

  // Assigns through an explicit branch rather than a computed key so the
  // resulting object stays a fully typed UserPreferences.
  const setThreadLimit = (key: ThreadLimitKey, threadCount: number | null) => {
    setEditingPreferences((previous) => {
      if (!previous) return previous;
      return key === 'steamMaxThreadCount'
        ? { ...previous, steamMaxThreadCount: threadCount }
        : { ...previous, epicMaxThreadCount: threadCount };
    });
  };

  const handleSavePreferences = async () => {
    if (!editingSession || !editingPreferences) return;

    try {
      setSavingPreferences(true);
      await ApiService.saveSessionPreferences<void>(editingSession.id, editingPreferences);

      const isOwnSession = editingSession.isCurrentSession;

      if (isOwnSession) {
        if (editingPreferences.selectedTheme) {
          await themeService.setTheme(editingPreferences.selectedTheme);
        }
        await themeService.setSharpCorners(editingPreferences.sharpCorners);
        await themeService.setDisableTooltips(editingPreferences.disableTooltips);
        await themeService.setDisableStickyNotifications(
          editingPreferences.disableStickyNotifications
        );
        await themeService.setPicsAlwaysVisible(editingPreferences.picsAlwaysVisible);
      }

      if (isGuestSession(editingSession)) {
        await ApiService.setSessionRefreshRate(
          editingSession.id,
          editingPreferences.refreshRate || ''
        );

        const prefillToggles: { service: GameServiceId; enabled: boolean }[] = [];
        for (const service of PREFILL_SERVICES) {
          const pending = pendingPrefillChanges[service.id];
          if (pending !== null) {
            prefillToggles.push({ service: service.id, enabled: pending });
          }
        }
        await Promise.all(
          prefillToggles.map(({ service, enabled }: { service: GameServiceId; enabled: boolean }) =>
            ApiService.toggleGuestPrefillService(editingSession.id, service, enabled)
          )
        );
      }

      setEditingSession(null);
      setEditingPreferences(null);
      setPendingPrefillChanges(NO_PENDING_PREFILL_CHANGES);
      loadSessions(false);
    } catch (err: unknown) {
      notifyError(t('activeSessions.errors.savePreferences'), err, {
        logLabel: 'Failed to save session preferences'
      });
    } finally {
      setSavingPreferences(false);
    }
  };

  const handleBulkResetToDefaults = async () => {
    try {
      setBulkActionInProgress('reset');
      const data = await ApiService.bulkResetSessionsToDefaults<{ affectedCount: number }>();
      showToast('success', t('user.bulkActions.resetSuccess', { count: data.affectedCount }));
      setShowBulkResetConfirm(false);
    } catch (err: unknown) {
      notifyError(t('user.bulkActions.errors.resetFailed'), err, {
        logLabel: 'Failed to reset sessions to defaults'
      });
    } finally {
      setBulkActionInProgress(null);
    }
  };

  const handleClearAllGuests = async () => {
    try {
      setBulkActionInProgress('clear');
      const data = await ApiService.bulkClearGuestSessions<{ clearedCount: number }>();
      showToast('success', t('user.bulkActions.clearSuccess', { count: data.clearedCount }));
      onSessionsChange();
      setShowClearGuestsConfirm(false);
    } catch (err: unknown) {
      notifyError(t('user.bulkActions.errors.clearFailed'), err, {
        logLabel: 'Failed to clear guest sessions'
      });
    } finally {
      setBulkActionInProgress(null);
    }
  };

  // ============================================================
  // SignalR Handlers
  // ============================================================

  const handleSessionRevoked = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionDeleted = useCallback(
    (data: { sessionId: string; sessionType: string }) => {
      setSessions((prev) => prev.filter((s: Session) => s.id !== data.sessionId));
    },
    [setSessions]
  );

  const handleSessionsCleared = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionCreated = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionLastSeenUpdated = useCallback(
    (data: { sessionId: string; lastSeenAt: string }) => {
      setSessions((prev) =>
        prev.map((session: Session) => {
          if (session.id === data.sessionId) {
            return { ...session, lastSeenAt: data.lastSeenAt };
          }
          return session;
        })
      );
    },
    [setSessions]
  );

  const handleGuestDurationUpdated = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handlePrefillPermissionChanged = useCallback(
    (data: {
      sessionId: string;
      enabled: boolean;
      prefillExpiresAt?: string;
      service?: string;
    }) => {
      // An absent or unrecognised service id resolves to Steam, matching the events sent
      // before the permission was split into one field per service.
      const service = prefillServiceConfig(data.service ?? '');
      setSessions((prev: Session[]) =>
        prev.map((s: Session) =>
          s.id !== data.sessionId
            ? s
            : {
                ...s,
                [service.sessionEnabledField]: data.enabled,
                [service.sessionExpiresAtField]: data.prefillExpiresAt || null
              }
        )
      );
    },
    [setSessions]
  );

  const handleUserPreferencesReset = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleGuestPrefillConfigChanged = useCallback(
    (data: { maxThreadCount?: number | null }) => {
      loadSessions(false);
      if ('maxThreadCount' in data) {
        setDefaultGuestMaxThreadCount(data.maxThreadCount ?? null);
      }
    },
    [loadSessions]
  );

  const handleEpicGuestPrefillConfigChanged = useCallback(
    (data: EpicGuestPrefillConfigChangedEvent) => {
      loadSessions(false);
      if ('epicMaxThreadCount' in data) {
        setEpicDefaultGuestMaxThreadCount(data.epicMaxThreadCount ?? null);
      }
    },
    [loadSessions]
  );

  // Battle.net is anonymous (no thread limit); refresh sessions on config change.
  const handleBattlenetGuestPrefillConfigChanged = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleRiotGuestPrefillConfigChanged = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  // Xbox is login-required (mirrors Epic, has a thread limit); refresh sessions on config change.
  const handleXboxGuestPrefillConfigChanged = useCallback(
    (_data: XboxGuestPrefillConfigChangedEvent) => {
      loadSessions(false);
    },
    [loadSessions]
  );

  const handleGuestRefreshRateUpdated = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  // ============================================================
  // Helper Functions
  // ============================================================

  // Exclusive expansion: opening a session closes any other, so the list reads
  // as one detail at a time. The Set shape stays because the lazy pref-loading
  // effect iterates it.
  const toggleSessionExpanded = (sessionId: string) => {
    setExpandedSessions((prev) => (prev.has(sessionId) ? new Set() : new Set([sessionId])));
  };

  const handleRevokeSession = (session: Session) => {
    setPendingRevokeSession(session);
  };

  const handleDeleteSession = (session: Session) => {
    setPendingDeleteSession(session);
  };

  const formatTimeRemaining = (expiresAt: string) => {
    const now = new Date();
    const expiry = parseUtcDate(expiresAt);
    const diff = expiry.getTime() - now.getTime();

    if (diff <= 0) return t('activeSessions.prefill.status.expired');

    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));

    if (hours > 0) {
      return t('activeSessions.prefill.status.hoursMinutesRemaining', {
        hours,
        minutes
      });
    }
    return t('activeSessions.prefill.status.minutesRemaining', {
      minutes
    });
  };

  // i18n relative time for the row's last-seen meta (module getRelativeTime is
  // English-only; this threads t so the copy is translatable).
  const formatRelativeTime = (dateString: string | null): string => {
    if (!dateString) return t('activeSessions.relative.never');
    const now = new Date();
    const date = parseUtcDate(dateString);
    const diffSecs = Math.floor((now.getTime() - date.getTime()) / 1000);
    if (diffSecs < 60) return t('activeSessions.relative.justNow');
    const diffMins = Math.floor(diffSecs / 60);
    if (diffMins < 60) return t('activeSessions.relative.minutesAgo', { count: diffMins });
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return t('activeSessions.relative.hoursAgo', { count: diffHours });
    const diffDays = Math.floor(diffHours / 24);
    return t('activeSessions.relative.daysAgo', { count: diffDays });
  };

  type SessionStatus = 'active' | 'away' | 'inactive';

  const getSessionStatus = (session: Session): SessionStatus => {
    if (session.isRevoked || session.isExpired) return 'inactive';

    // Presence gate from the unified activity registry (one ActivityUpdated event drives every dot).
    // The backend reconciles this against the database on a timer, so once a snapshot has arrived it is
    // authoritative for every valid session (not just ones created by this process). Before that first
    // snapshot lands, default to present rather than guessing the registry means "gone". The last-seen
    // aging below still decides active vs away.
    const present = activity.isActiveOrFallback('userSession', session.id, 'present', true);
    if (!present) return 'inactive';

    if (session.isCurrentSession && isLocallyActive) {
      return 'active';
    }

    if (!session.lastSeenAt) return 'inactive';

    const now = new Date();
    const lastSeen = parseUtcDate(session.lastSeenAt);
    const diffSeconds = (now.getTime() - lastSeen.getTime()) / 1000;

    if (diffSeconds <= ACTIVE_MAX_AGE_SECONDS) return 'active';
    if (diffSeconds <= AWAY_MAX_AGE_SECONDS) return 'away';
    return 'inactive';
  };

  const getCountForFilter = (filter: SessionFilter): number => {
    if (filter === 'all') return activeSessions.length;
    if (filter === 'admin') return activeSessions.filter((s: Session) => isAdminSession(s)).length;
    return activeSessions.filter((s: Session) => isGuestSession(s)).length;
  };

  const getFilterLabel = (filter: SessionFilter): string => {
    if (filter === 'all') return t('activeSessions.filters.all');
    if (filter === 'admin') return t('activeSessions.filters.admin');
    return t('activeSessions.filters.guest');
  };

  // ============================================================
  // useEffect Hooks
  // ============================================================

  // Lazy-load per-session preferences when a row is expanded (never during
  // render). Fires once per newly-opened, still-live session; the loaded/loading
  // guards dedupe against the periodic status tick and re-renders.
  useEffect(() => {
    expandedSessions.forEach((id: string) => {
      const s = sessions.find((x: Session) => x.id === id);
      if (!s || s.isRevoked || s.isExpired) return;
      if (!isPreferencesLoaded(id) && !isPreferencesLoading(id)) {
        loadSessionPreferences(id);
      }
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [expandedSessions, sessions]);

  // Periodic tick so getSessionStatus() re-evaluates against a fresh "now".
  // Without this, the status dot only changes when an unrelated render fires,
  // producing the "sometimes active / sometimes away when I click" flicker.
  useEffect(() => {
    const id = setInterval(() => {
      setStatusTick((t) => (t + 1) % 1_000_000);
    }, 15_000);
    return () => clearInterval(id);
  }, []);

  // Load default guest max thread count for both Steam and Epic
  useEffect(() => {
    const loadThreadConfig = async () => {
      try {
        const [steamData, epicData] = await Promise.all([
          ApiService.getGuestPrefillConfig<{ maxThreadCount: number | null }>('prefill'),
          ApiService.getGuestPrefillConfig<{ maxThreadCount: number | null }>('epic-prefill')
        ]);
        setDefaultGuestMaxThreadCount(steamData.maxThreadCount ?? null);
        setEpicDefaultGuestMaxThreadCount(epicData.maxThreadCount ?? null);
      } catch (err) {
        notifyError(t('user.errors.loadThreadConfig'), err, {
          logLabel: 'Failed to load thread config'
        });
      }
    };
    loadThreadConfig();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // SignalR subscriptions + initial load
  useEffect(() => {
    loadSessions(true);

    on('UserSessionRevoked', handleSessionRevoked);
    on('UserSessionDeleted', handleSessionDeleted);
    on('UserSessionsCleared', handleSessionsCleared);
    on('UserSessionCreated', handleSessionCreated);
    on('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
    on('GuestDurationUpdated', handleGuestDurationUpdated);
    on('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);
    on('UserPreferencesReset', handleUserPreferencesReset);
    on('GuestPrefillConfigChanged', handleGuestPrefillConfigChanged);
    on('EpicGuestPrefillConfigChanged', handleEpicGuestPrefillConfigChanged);
    on('BattleNetGuestPrefillConfigChanged', handleBattlenetGuestPrefillConfigChanged);
    on('RiotGuestPrefillConfigChanged', handleRiotGuestPrefillConfigChanged);
    on('XboxGuestPrefillConfigChanged', handleXboxGuestPrefillConfigChanged);
    on('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);

    return () => {
      off('UserSessionRevoked', handleSessionRevoked);
      off('UserSessionDeleted', handleSessionDeleted);
      off('UserSessionsCleared', handleSessionsCleared);
      off('UserSessionCreated', handleSessionCreated);
      off('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
      off('GuestDurationUpdated', handleGuestDurationUpdated);
      off('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);
      off('UserPreferencesReset', handleUserPreferencesReset);
      off('GuestPrefillConfigChanged', handleGuestPrefillConfigChanged);
      off('EpicGuestPrefillConfigChanged', handleEpicGuestPrefillConfigChanged);
      off('BattleNetGuestPrefillConfigChanged', handleBattlenetGuestPrefillConfigChanged);
      off('RiotGuestPrefillConfigChanged', handleRiotGuestPrefillConfigChanged);
      off('XboxGuestPrefillConfigChanged', handleXboxGuestPrefillConfigChanged);
      off('GuestRefreshRateUpdated', handleGuestRefreshRateUpdated);
    };
  }, [
    loadSessions,
    on,
    off,
    handleSessionRevoked,
    handleSessionDeleted,
    handleSessionsCleared,
    handleSessionCreated,
    handleSessionLastSeenUpdated,
    handleGuestDurationUpdated,
    handlePrefillPermissionChanged,
    handleUserPreferencesReset,
    handleGuestPrefillConfigChanged,
    handleEpicGuestPrefillConfigChanged,
    handleBattlenetGuestPrefillConfigChanged,
    handleRiotGuestPrefillConfigChanged,
    handleXboxGuestPrefillConfigChanged,
    handleGuestRefreshRateUpdated
  ]);

  // Re-fetch when parent triggers a refresh via refreshKey
  useEffect(() => {
    if (refreshKey !== undefined && refreshKey > 0) {
      loadSessions(false);
    }
  }, [refreshKey, loadSessions]);

  // Recover a stale snapshot after a reconnect: a session change event can be missed while
  // the socket is down, so resync the sessions view whenever the connection returns.
  useReconnectRefetch(isConnected, () => loadSessions(false));

  // ============================================================
  // Derived Data
  // ============================================================

  // Sessions from API are already active-only (paginated); history comes separately
  const activeSessions = sessions;

  const typeFilteredSessions =
    activeFilterValue === 'all'
      ? activeSessions
      : activeFilterValue === 'admin'
        ? activeSessions.filter((s: Session) => isAdminSession(s))
        : activeSessions.filter((s: Session) => isGuestSession(s));

  const filteredActiveSessions = searchQuery.trim()
    ? typeFilteredSessions.filter((s: Session) => sessionMatchesSearch(s, searchQuery))
    : typeFilteredSessions;

  const pageSizeOptions = PAGE_SIZE_OPTIONS.map((size: SessionPageSize) => ({
    value: String(size),
    label: String(size)
  }));

  // Client-side pagination over the filtered group. Clamp the page so deleting
  // sessions (or a stale page) can never point past the end.
  const totalPages = Math.max(1, Math.ceil(filteredActiveSessions.length / pageSize));
  const safePage = Math.min(currentPage, totalPages);
  const pagedSessions = filteredActiveSessions.slice(
    (safePage - 1) * pageSize,
    safePage * pageSize
  );

  // ============================================================
  // Render Helpers: Session Row (one responsive item — desktop + mobile)
  // ============================================================

  const renderSessionItem = (session: Session) => {
    const sessionStatus = getSessionStatus(session);
    const parsedUA = parseUserAgent(session.userAgent);
    const isExpanded = expandedSessions.has(session.id);
    const admin = isAdminSession(session);
    const guest = isGuestSession(session);
    const canRevoke =
      guest && !session.isRevoked && !session.isExpired && !session.isCurrentSession;
    const canShowRemaining = guest && !session.isRevoked && !session.isExpired;

    const prefs = getSessionPreferences(session.id);
    const isLoadingPrefs = isPreferencesLoading(session.id);
    const themeName = prefs?.selectedTheme
      ? availableThemes.find((th: ThemeOption) => th.id === prefs.selectedTheme)?.name ||
        prefs.selectedTheme
      : t('activeSessions.preferencesModal.defaultThemeShort');
    // UTC is a third clock rather than a variation on local-vs-server, so it is tested first;
    // a two-way ternary reports every UTC session as being on the server's clock.
    const timezoneLabel = prefs?.useUtcTimezone
      ? t('activeSessions.labels.utc')
      : prefs?.useLocalTimezone
        ? t('activeSessions.labels.local')
        : t('activeSessions.labels.server');

    const flag = countryCodeToFlag(session.countryCode);
    const location = formatLocation(session.city, session.regionName, session.countryName);
    const hasClientInfo = Boolean(
      session.publicIpAddress ||
      location ||
      session.ispName ||
      session.timezone ||
      session.browserLanguage ||
      session.screenResolution
    );

    return (
      <div key={session.id} className="session-item">
        <div
          className="mgmt-row mgmt-row--interactive focus-ring--inset session-row"
          role="button"
          tabIndex={0}
          aria-expanded={isExpanded}
          onClick={() => toggleSessionExpanded(session.id)}
          onKeyDown={(e: React.KeyboardEvent) => {
            if (e.key === 'Enter' || e.key === ' ') {
              e.preventDefault();
              toggleSessionExpanded(session.id);
            }
          }}
        >
          <StatusDot state={sessionStatus} label={t(`activeSessions.status.${sessionStatus}`)} />
          <div className="mgmt-row__body">
            <div className="session-row__titleline">
              <Tooltip content={parsedUA.title} position="top" className="block min-w-0">
                <span className="mgmt-row__title block truncate">{parsedUA.title}</span>
              </Tooltip>
              <span
                className={`themed-badge session-type-badge ${admin ? 'session-badge-user' : 'session-badge-guest'}`}
              >
                {admin
                  ? t('activeSessions.labels.userBadge')
                  : t('activeSessions.labels.guestBadge')}
              </span>
              {session.isCurrentSession && (
                <span className="session-you">({t('activeSessions.currentSessionShort')})</span>
              )}
            </div>
            <div className="mgmt-row__meta session-row__meta">
              {session.ipAddress && (
                <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} />
              )}
              <span>{formatRelativeTime(session.lastSeenAt)}</span>
              {canShowRemaining && <span>{formatTimeRemaining(session.expiresAt)}</span>}
              {session.isRevoked && (
                <span className="is-error">{t('activeSessions.status.revoked')}</span>
              )}
              {session.isExpired && !session.isRevoked && (
                <span className="is-warning">{t('activeSessions.prefill.status.expired')}</span>
              )}
            </div>
          </div>
          <div
            className="mgmt-row__actions session-row__actions"
            onClick={(e: React.MouseEvent) => e.stopPropagation()}
          >
            {/* One width for every button in the row, so the cluster does not step in and out as
                labels change length or as a label swaps to its in-progress text. 5rem clears the
                longest of those, "Revoking...", measured at 59px plus 16px of padding.
                The box differs between the two views, so the type follows it. On a pointer view
                these are xs at a 28px box with the recipe's 12px label. Below the breakpoint the
                row rule grows them to the 44px touch floor, where a 12px label reads as a mis-set
                control, so the type steps up. The height bump is scoped to the pointer view on
                purpose: as `!important` it would outrank that rule and cost the touch target. */}
            <Button
              variant="default"
              color="blue"
              size="xs"
              className="w-20 sm:!min-h-7 max-sm:!text-sm"
              onClick={() => handleEditSession(session)}
            >
              {t('actions.edit')}
            </Button>
            {session.isCurrentSession && (
              <Button
                variant="default"
                color="orange"
                size="xs"
                className="w-20 sm:!min-h-7 max-sm:!text-sm"
                onClick={handleLogout}
                disabled={loggingOut}
                loading={loggingOut}
              >
                {t('activeSessions.actions.logout')}
              </Button>
            )}
            {canRevoke && (
              <Button
                variant="default"
                color="orange"
                size="xs"
                className="w-20 sm:!min-h-7 max-sm:!text-sm"
                onClick={() => handleRevokeSession(session)}
                disabled={revokingSession === session.id}
              >
                {revokingSession === session.id
                  ? t('activeSessions.actions.revoking')
                  : t('activeSessions.actions.revoke')}
              </Button>
            )}
            {!session.isCurrentSession && (
              <Button
                variant="filled"
                color="red"
                size="xs"
                className="w-20 sm:!min-h-7 max-sm:!text-sm"
                onClick={() => handleDeleteSession(session)}
                disabled={deletingSession === session.id}
              >
                {deletingSession === session.id
                  ? t('activeSessions.actions.deleting')
                  : t('activeSessions.actions.delete')}
              </Button>
            )}
          </div>
          <ChevronDown
            className={`session-row__chevron ${isExpanded ? 'is-open' : ''}`}
            aria-hidden="true"
          />
        </div>

        <CollapsibleRegion open={isExpanded} contentClassName="mgmt-row-detail">
          <div className="session-detail">
            {hasClientInfo && (
              <div className="mgmt-stat-grid">
                {session.publicIpAddress && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.publicIp')}
                    </span>
                    <span className="mgmt-stat__value">
                      {cleanIpAddress(session.publicIpAddress)}
                    </span>
                  </div>
                )}
                {location && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.location')}
                    </span>
                    <span className="mgmt-stat__value">
                      {flag && <span aria-hidden="true">{flag} </span>}
                      {location}
                    </span>
                  </div>
                )}
                {session.ispName && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.isp')}
                    </span>
                    <span className="mgmt-stat__value">{session.ispName}</span>
                  </div>
                )}
                {session.timezone && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.timezoneHeading')}
                    </span>
                    <span className="mgmt-stat__value">{session.timezone}</span>
                  </div>
                )}
                {session.browserLanguage && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.language')}
                    </span>
                    <span className="mgmt-stat__value">{session.browserLanguage}</span>
                  </div>
                )}
                {session.screenResolution && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.screen')}
                    </span>
                    <span className="mgmt-stat__value">{session.screenResolution}</span>
                  </div>
                )}
              </div>
            )}

            {!session.isRevoked && !session.isExpired && (
              <div className="space-y-2">
                <p className="mgmt-subhead caps-label">{t('activeSessions.labels.preferences')}</p>
                {isLoadingPrefs ? (
                  <div className="flex items-center gap-2 text-xs text-themed-muted">
                    <LoadingSpinner inline size="xs" />
                    {t('activeSessions.preferencesModal.loading')}
                  </div>
                ) : prefs ? (
                  <div className="mgmt-stat-grid">
                    <div className="mgmt-stat">
                      <span className="mgmt-stat__label caps-label caps-label--sm">
                        {t('activeSessions.labels.theme')}
                      </span>
                      <span className="mgmt-stat__value">{themeName}</span>
                    </div>
                    <div className="mgmt-stat">
                      <span className="mgmt-stat__label caps-label caps-label--sm">
                        {t('activeSessions.labels.timezoneHeading')}
                      </span>
                      <span className="mgmt-stat__value">{timezoneLabel}</span>
                    </div>
                    {prefs.sharpCorners && (
                      <div className="mgmt-stat">
                        <span className="mgmt-stat__label caps-label caps-label--sm">
                          {t('user.guest.preferences.sharpCorners.label')}
                        </span>
                        <span className="mgmt-stat__value">
                          {t('activeSessions.prefill.status.enabled')}
                        </span>
                      </div>
                    )}
                    {!prefs.showDatasourceLabels && (
                      <div className="mgmt-stat">
                        <span className="mgmt-stat__label caps-label caps-label--sm">
                          {t('user.guest.preferences.datasourceLabels.label')}
                        </span>
                        <span className="mgmt-stat__value">
                          {t('activeSessions.prefill.status.disabled')}
                        </span>
                      </div>
                    )}
                  </div>
                ) : null}
              </div>
            )}

            {guest && !session.isRevoked && !session.isExpired && (
              <div className="space-y-2">
                <p className="mgmt-subhead caps-label">{t('activeSessions.prefill.title')}</p>
                <div className="session-prefill-readout">
                  {PREFILL_SERVICES.map((service: PrefillServiceConfig) => {
                    const ServiceIcon = service.icon;
                    return (
                      <span
                        key={service.id}
                        className={`session-prefill-svc ${session[service.sessionEnabledField] ? 'is-enabled' : 'is-disabled'}`}
                      >
                        <ServiceIcon size={12} />
                        {service.shortName}
                      </span>
                    );
                  })}
                </div>
              </div>
            )}

            <p className="mgmt-scanmeta session-detail__id">
              {t('activeSessions.labels.sessionIdWithValue', { id: session.id })}
            </p>

            {/* Lifecycle status pinned to the bottom as a footer strip (divider
                above), matching the app-wide .dash-readout--footer convention. */}
            <div className="dash-readout dash-readout--footer">
              <div className="dash-readout-item">
                <span className="dash-readout-value">
                  <FormattedTimestamp timestamp={session.createdAt} />
                </span>
                <span className="caps-label caps-label--wide dash-readout-label">
                  {t('activeSessions.labels.createdShort')}
                </span>
              </div>
              <div className="dash-readout-item">
                <span className="dash-readout-value">
                  {session.lastSeenAt ? (
                    <FormattedTimestamp timestamp={session.lastSeenAt} />
                  ) : (
                    t('activeSessions.labels.never')
                  )}
                </span>
                <span className="caps-label caps-label--wide dash-readout-label">
                  {t('activeSessions.labels.lastSeenShort')}
                </span>
              </div>
              <div className="dash-readout-item">
                <span className="dash-readout-value">
                  {admin ? (
                    t('activeSessions.labels.never')
                  ) : (
                    <FormattedTimestamp timestamp={session.expiresAt} />
                  )}
                </span>
                <span className="caps-label caps-label--wide dash-readout-label">
                  {t('activeSessions.labels.expires')}
                </span>
              </div>
              {session.revokedAt && (
                <div className="dash-readout-item">
                  <span className="dash-readout-value is-error">
                    <FormattedTimestamp timestamp={session.revokedAt} />
                  </span>
                  <span className="caps-label caps-label--wide dash-readout-label">
                    {t('activeSessions.labels.revokedShort')}
                  </span>
                </div>
              )}
            </div>
          </div>
        </CollapsibleRegion>
      </div>
    );
  };

  // ============================================================
  // Render Helpers: History Card
  // ============================================================

  const renderHistoryCard = (session: Session) => {
    const parsedUA = parseUserAgent(session.userAgent);
    const admin = isAdminSession(session);

    return (
      <div key={session.id} className="mgmt-row">
        <div className="mgmt-row__body">
          <div className="session-row__titleline">
            <Tooltip content={parsedUA.title} position="top" className="block min-w-0">
              <span className="mgmt-row__title block truncate">{parsedUA.title}</span>
            </Tooltip>
            <span
              className={`themed-badge session-type-badge ${admin ? 'session-badge-user' : 'session-badge-guest'}`}
            >
              {admin ? t('activeSessions.labels.userBadge') : t('activeSessions.labels.guestBadge')}
            </span>
            {session.isRevoked && (
              <Badge variant="error" className="session-type-badge">
                {t('activeSessions.status.revoked')}
              </Badge>
            )}
            {session.isExpired && !session.isRevoked && (
              <Badge variant="warning" className="session-type-badge">
                {t('activeSessions.prefill.status.expired')}
              </Badge>
            )}
          </div>
          <div className="mgmt-row__meta session-row__meta">
            {session.ipAddress && <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} />}
            <span>
              <FormattedTimestamp timestamp={session.createdAt} />
            </span>
            {session.revokedAt && (
              <span className="is-error">
                {t('activeSessions.labels.revokedAt')}{' '}
                <FormattedTimestamp timestamp={session.revokedAt} />
              </span>
            )}
            <span className="session-detail__id">{session.id}</span>
          </div>
        </div>
        <div className="mgmt-row__actions">
          <Button
            variant="filled"
            color="red"
            size="sm"
            leftSection={<Trash2 className="w-4 h-4" />}
            onClick={() => handleDeleteSession(session)}
            disabled={deletingSession === session.id}
            loading={deletingSession === session.id}
          >
            {t('activeSessions.actions.delete')}
          </Button>
        </div>
      </div>
    );
  };

  // ============================================================
  // Render: Main
  // ============================================================

  return (
    <div className="session-console">
      <div>
        <div className="flex items-center justify-between gap-2 mb-3 sm:mb-4">
          <div className="flex items-center gap-2 min-w-0">
            <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
            <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
              {t('user.groups.sessions')}
            </h3>
          </div>
          <AccordionGroupToggle />
        </div>

        <div className="space-y-4">
          <AccordionSection
            title={t('activeSessions.title')}
            titleAccessory={
              <HelpPopover width={320}>
                <HelpSection title={t('activeSessions.help.aboutTitle')}>
                  {t('activeSessions.summary')}
                </HelpSection>
                <HelpSection title={t('activeSessions.help.sessionTypes.title')} variant="subtle">
                  <HelpDefinition
                    items={[
                      {
                        term: t('activeSessions.help.sessionTypes.authenticated.label'),
                        description: t('activeSessions.help.sessionTypes.authenticated.description')
                      },
                      {
                        term: t('activeSessions.help.sessionTypes.guest.label'),
                        description: t('activeSessions.help.sessionTypes.guest.description', {
                          hours: guestDurationHours
                        })
                      }
                    ]}
                  />
                </HelpSection>
                <HelpSection title={t('activeSessions.help.actions.title')} variant="subtle">
                  <HelpDefinition
                    items={[
                      {
                        term: t('activeSessions.help.actions.revoke.label'),
                        description: t('activeSessions.help.actions.revoke.description')
                      },
                      {
                        term: t('activeSessions.help.actions.delete.label'),
                        description: t('activeSessions.help.actions.delete.description')
                      }
                    ]}
                  />
                </HelpSection>
              </HelpPopover>
            }
            icon={Users}
            iconColor="var(--theme-icon-blue)"
            count={
              !loading && activeSessions.length > 0 ? filteredActiveSessions.length : undefined
            }
            isExpanded={sessionsExpanded}
            onToggle={() => setSessionsExpanded((prev: boolean) => !prev)}
            badge={
              <SectionHeaderActions>
                <SectionHeaderChip variant={guestModeLocked ? 'error' : 'success'}>
                  {guestModeLocked
                    ? t('activeSessions.toggle.locked')
                    : t('activeSessions.toggle.unlocked')}
                </SectionHeaderChip>
                <SectionActionsMenu label={t('management.actions.menuLabel')} width="w-56">
                  {(close) => (
                    <>
                      <ActionMenuItem
                        icon={<RotateCcw className="w-4 h-4" />}
                        onClick={() => {
                          close();
                          setShowBulkResetConfirm(true);
                        }}
                      >
                        {t('user.bulkActions.buttons.reset')}
                      </ActionMenuItem>
                      <ActionMenuDivider />
                      <ActionMenuDangerItem
                        icon={<Eraser className="w-4 h-4" />}
                        onClick={() => {
                          close();
                          setShowClearGuestsConfirm(true);
                        }}
                      >
                        {t('user.bulkActions.buttons.clear')}
                      </ActionMenuDangerItem>
                    </>
                  )}
                </SectionActionsMenu>
              </SectionHeaderActions>
            }
          >
            <div className="space-y-4">
              {/* Guest lock control stays visible (not buried in kebab); filters stay in-body */}
              <div className="mgmt-toolbar session-toolbar">
                {!loading && activeSessions.length > 0 ? (
                  <div className="session-filter-cluster cluster">
                    {(['all', 'admin', 'guest'] as const).map((filter: SessionFilter) => {
                      const isActive = activeFilterValue === filter;
                      return (
                        <Button
                          key={filter}
                          variant={isActive ? 'filled' : 'default'}
                          color={isActive ? 'blue' : 'gray'}
                          size="sm"
                          aria-pressed={isActive}
                          onClick={() => setActiveFilter(filter)}
                          rightSection={
                            <span
                              className={`themed-badge badge-count ${isActive ? 'badge-count-on-color' : 'status-badge-neutral'}`}
                            >
                              {getCountForFilter(filter)}
                            </span>
                          }
                        >
                          {getFilterLabel(filter)}
                        </Button>
                      );
                    })}

                    <div className="session-filter-search relative">
                      <Search className="input-icon absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-themed-muted" />
                      <input
                        type="text"
                        value={searchQuery}
                        onChange={handleSearchChange}
                        placeholder={t('activeSessions.searchPlaceholder')}
                        aria-label={t('activeSessions.searchPlaceholder')}
                        className="themed-input input-search-sm w-full pl-10 pr-12"
                      />
                      {searchQuery ? (
                        <button
                          type="button"
                          onClick={handleClearSearch}
                          className="absolute right-3 top-1/2 -translate-y-1/2 text-themed-muted hover:text-themed-primary text-xs"
                        >
                          {t('common.clear')}
                        </button>
                      ) : null}
                    </div>

                    <EnhancedDropdown
                      variant="button"
                      size="sm"
                      options={pageSizeOptions}
                      value={String(pageSize)}
                      onChange={handlePageSizeChange}
                      prefix={t('downloads.tab.filters.showPrefix')}
                      className="session-filter-pagesize"
                      dropdownWidth="100px"
                    />
                  </div>
                ) : (
                  <div />
                )}

                <div className="session-toolbar__right">
                  <ToggleSwitch
                    options={[
                      {
                        value: 'unlocked',
                        label: t('activeSessions.toggle.unlocked'),
                        icon: <Unlock />,
                        activeColor: 'success'
                      },
                      {
                        value: 'locked',
                        label: t('activeSessions.toggle.locked'),
                        icon: <Lock />,
                        activeColor: 'error'
                      }
                    ]}
                    value={guestModeLocked ? 'locked' : 'unlocked'}
                    onChange={onToggleGuestLock}
                    disabled={updatingGuestLock}
                    loading={updatingGuestLock}
                    title={
                      guestModeLocked
                        ? t('activeSessions.toggle.lockedTitle')
                        : t('activeSessions.toggle.unlockedTitle')
                    }
                  />
                </div>
              </div>

              {loading && (
                <LoadingState message={t('activeSessions.loading')} shape="list" rows={4} />
              )}

              {!loading && activeSessions.length === 0 && (
                <EmptyState
                  variant="panel"
                  icon={Users}
                  title={t('activeSessions.empty.title')}
                  subtitle={t('activeSessions.empty.subtitle')}
                />
              )}

              {!loading && filteredActiveSessions.length > 0 && (
                <div className="mgmt-list divided-list">{pagedSessions.map(renderSessionItem)}</div>
              )}

              {!loading && activeSessions.length > 0 && filteredActiveSessions.length === 0 && (
                <EmptyState
                  variant="panel"
                  icon={Users}
                  title={t('activeSessions.empty.filteredTitle')}
                  subtitle={t('activeSessions.empty.filtered')}
                />
              )}

              {!loading && totalPages > 1 && (
                <Pagination
                  currentPage={safePage}
                  totalPages={totalPages}
                  totalItems={filteredActiveSessions.length}
                  itemsPerPage={pageSize}
                  onPageChange={(newPage: number) => setCurrentPage(newPage)}
                  itemLabel={t('activeSessions.paginationLabel')}
                  showCard={false}
                />
              )}
            </div>
          </AccordionSection>

          {!loading && historySessions.length > 0 && (
            <AccordionSection
              title={t('activeSessions.history.title')}
              titleAccessory={
                <HelpPopover position="left" width={320}>
                  <HelpSection title={t('activeSessions.history.help.aboutTitle')}>
                    {t('activeSessions.history.summary')}
                  </HelpSection>
                </HelpPopover>
              }
              icon={History}
              iconColor="var(--theme-icon-purple)"
              count={historySessions.length}
              isExpanded={historyExpanded}
              onToggle={() => setHistoryExpanded((prev: boolean) => !prev)}
            >
              <div className="mgmt-list divided-list">{historySessions.map(renderHistoryCard)}</div>
            </AccordionSection>
          )}
        </div>
      </div>

      {/* ============================================================ */}
      {/* Modals */}
      {/* ============================================================ */}

      {/* Revoke Session Modal */}
      <ConfirmationModal
        opened={!!pendingRevokeSession}
        onClose={() => setPendingRevokeSession(null)}
        onConfirm={confirmRevokeSession}
        title={t('activeSessions.revokeModal.title')}
        confirmLabel={t('activeSessions.revokeModal.confirm')}
        confirmColor="orange"
        loading={!!revokingSession}
      >
        <p className="text-themed-secondary">
          {t('activeSessions.revokeModal.message', {
            type:
              pendingRevokeSession && isAdminSession(pendingRevokeSession)
                ? t('activeSessions.sessionTypes.authenticatedUser')
                : t('activeSessions.sessionTypes.guestUser')
          })}
        </p>

        {pendingRevokeSession && (
          <div className="mgmt-panel">
            <p className="text-sm text-themed-primary font-medium">
              {parseUserAgent(pendingRevokeSession.userAgent).title}
            </p>
            <p className="text-xs text-themed-muted font-mono">
              {t('activeSessions.labels.sessionIdWithValue', { id: pendingRevokeSession.id })}
            </p>
          </div>
        )}

        <Alert color="yellow">
          <p className="text-sm">{t('activeSessions.revokeModal.summary')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Delete Session Modal - a red trash separates the permanent delete from the revoke above,
          which only ends the session and keeps its history. */}
      <ConfirmationModal
        opened={!!pendingDeleteSession}
        onClose={() => setPendingDeleteSession(null)}
        onConfirm={confirmDeleteSession}
        title={t('activeSessions.deleteModal.title')}
        icon={<Trash2 className="w-6 h-6 text-themed-error" />}
        confirmLabel={t('activeSessions.deleteModal.confirm')}
        loading={!!deletingSession}
      >
        <p className="text-themed-secondary">
          {t('activeSessions.deleteModal.message', {
            type:
              pendingDeleteSession && isAdminSession(pendingDeleteSession)
                ? t('activeSessions.sessionTypes.authenticatedDevice')
                : t('activeSessions.sessionTypes.guestDevice')
          })}
        </p>

        {pendingDeleteSession && (
          <div className="mgmt-panel">
            <p className="text-sm text-themed-primary font-medium">
              {parseUserAgent(pendingDeleteSession.userAgent).title}
            </p>
            <p className="text-xs text-themed-muted font-mono">
              {t('activeSessions.labels.sessionIdWithValue', { id: pendingDeleteSession.id })}
            </p>
          </div>
        )}

        <Alert color="red">
          <p className="text-sm">{t('activeSessions.deleteModal.summary')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Bulk Reset Confirmation Modal */}
      <ConfirmationModal
        opened={showBulkResetConfirm}
        onClose={() => setShowBulkResetConfirm(false)}
        onConfirm={handleBulkResetToDefaults}
        title={t('user.bulkActions.resetModal.title')}
        icon={<RotateCcw className="w-6 h-6 text-themed-warning" />}
        confirmLabel={t('user.bulkActions.resetModal.confirm')}
        confirmColor="orange"
        loading={bulkActionInProgress === 'reset'}
        confirmDisabled={!!bulkActionInProgress}
      >
        <p className="text-themed-secondary">{t('user.bulkActions.resetModal.message')}</p>

        <Alert color="yellow">
          <p className="text-sm">{t('user.bulkActions.resetModal.summary')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Clear All Guests Confirmation Modal */}
      <ConfirmationModal
        opened={showClearGuestsConfirm}
        onClose={() => setShowClearGuestsConfirm(false)}
        onConfirm={handleClearAllGuests}
        title={t('user.bulkActions.clearModal.title')}
        icon={<Eraser className="w-6 h-6 text-themed-error" />}
        confirmLabel={t('user.bulkActions.clearModal.confirm')}
        loading={bulkActionInProgress === 'clear'}
        confirmDisabled={!!bulkActionInProgress}
      >
        <p className="text-themed-secondary">{t('user.bulkActions.clearModal.message')}</p>

        <Alert color="red">
          <p className="text-sm">{t('user.bulkActions.clearModal.summary')}</p>
        </Alert>
      </ConfirmationModal>

      {/* Edit User Preferences Modal */}
      <Modal
        opened={!!editingSession}
        onClose={() => {
          if (!savingPreferences) {
            setEditingSession(null);
            setEditingPreferences(null);
            setPendingPrefillChanges(NO_PENDING_PREFILL_CHANGES);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Edit className="w-6 h-6 text-themed-accent" />
            <span>{t('activeSessions.preferencesModal.title')}</span>
          </div>
        }
        size="lg"
      >
        <div className="space-y-4">
          {editingSession && (
            <div className="mgmt-panel">
              <p className="text-sm text-themed-primary font-medium">
                {parseUserAgent(editingSession.userAgent).title}
              </p>
              <p className="text-xs text-themed-muted">
                {isAdminSession(editingSession)
                  ? t('activeSessions.sessionTypes.authenticatedUser')
                  : t('activeSessions.sessionTypes.guestUser')}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                {t('activeSessions.labels.sessionIdWithValue', { id: editingSession.id })}
              </p>
            </div>
          )}

          {loadingPreferences && (
            <LoadingState
              message={t('activeSessions.preferencesModal.loading')}
              shape="fields"
              rows={4}
            />
          )}

          {!loadingPreferences && editingPreferences && (
            <div className="space-y-4">
              {/* Theme Selection */}
              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="text-sm font-medium text-themed-primary">
                    {t('activeSessions.preferencesModal.selectedTheme')}
                  </label>
                  {editingPreferences.selectedTheme &&
                  editingPreferences.selectedTheme !== defaultGuestTheme ? (
                    <button
                      type="button"
                      onClick={() =>
                        setEditingPreferences({
                          ...editingPreferences,
                          selectedTheme: null
                        })
                      }
                      className="text-xs px-2 py-0.5 rounded transition-colors text-themed-accent bg-themed-tertiary hover:bg-themed-secondary"
                    >
                      {t('actions.useDefault')}
                    </button>
                  ) : (
                    <Badge variant="neutral">{t('actions.usingDefault')}</Badge>
                  )}
                </div>
                <EnhancedDropdown
                  options={availableThemes.map((theme: ThemeOption) => ({
                    value: theme.id,
                    label: theme.name
                  }))}
                  value={editingPreferences.selectedTheme || defaultGuestTheme}
                  onChange={(value: string) =>
                    setEditingPreferences({
                      ...editingPreferences,
                      selectedTheme: value
                    })
                  }
                  className="w-full"
                />
                <p className="text-xs text-themed-muted mt-1">
                  {editingPreferences.selectedTheme
                    ? t('activeSessions.preferencesModal.customTheme')
                    : t('activeSessions.preferencesModal.defaultTheme', {
                        theme:
                          availableThemes.find((th: ThemeOption) => th.id === defaultGuestTheme)
                            ?.name || defaultGuestTheme
                      })}
                </p>
              </div>

              {/* Refresh Rate (Guest Users Only) */}
              {editingSession && isGuestSession(editingSession) && (
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <label className="text-sm font-medium text-themed-primary">
                      {t('activeSessions.preferencesModal.refreshRate')}
                    </label>
                    {editingPreferences.refreshRate &&
                    editingPreferences.refreshRate !== defaultGuestRefreshRate ? (
                      <button
                        type="button"
                        onClick={() =>
                          setEditingPreferences({
                            ...editingPreferences,
                            refreshRate: null
                          })
                        }
                        className="text-xs px-2 py-0.5 rounded transition-colors text-themed-accent bg-themed-tertiary hover:bg-themed-secondary"
                      >
                        {t('actions.useDefault')}
                      </button>
                    ) : (
                      <Badge variant="neutral">{t('actions.usingDefault')}</Badge>
                    )}
                  </div>
                  <EnhancedDropdown
                    options={translatedRefreshRateOptions}
                    value={editingPreferences.refreshRate || defaultGuestRefreshRate}
                    onChange={(value: string) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        refreshRate: value
                      })
                    }
                    className="w-full"
                  />
                  <p className="text-xs text-themed-muted mt-1">
                    {editingPreferences.refreshRate
                      ? t('activeSessions.preferencesModal.customRefreshRate')
                      : t('activeSessions.preferencesModal.defaultRefreshRate', {
                          rate:
                            translatedRefreshRateOptions.find(
                              (o: { value: string; label: string }) =>
                                o.value === defaultGuestRefreshRate
                            )?.label || defaultGuestRefreshRate
                        })}
                  </p>

                  {/* Per-session Refresh Rate Lock */}
                  <div className="mt-3 flex items-center justify-between p-3 rounded-lg bg-themed-tertiary">
                    <div className="flex items-center gap-2">
                      {editingPreferences.refreshRateLocked === false ? (
                        <Unlock className="w-4 h-4 text-themed-accent" />
                      ) : (
                        <Lock className="w-4 h-4 text-themed-muted" />
                      )}
                      <div>
                        <p className="text-sm text-themed-primary">
                          {t('activeSessions.preferencesModal.refreshLock.allow')}
                        </p>
                        <p className="text-xs text-themed-muted">
                          {editingPreferences.refreshRateLocked === null
                            ? t('activeSessions.preferencesModal.refreshLock.usingDefault')
                            : editingPreferences.refreshRateLocked
                              ? t('activeSessions.preferencesModal.refreshLock.locked')
                              : t('activeSessions.preferencesModal.refreshLock.unlocked')}
                        </p>
                      </div>
                    </div>
                    <div className="flex items-center gap-2">
                      {editingPreferences.refreshRateLocked !== null && (
                        <button
                          type="button"
                          onClick={() =>
                            setEditingPreferences({
                              ...editingPreferences,
                              refreshRateLocked: null
                            })
                          }
                          className="text-xs px-2 py-0.5 rounded transition-colors text-themed-accent bg-themed-secondary hover:bg-themed-hover"
                        >
                          {t('actions.useDefault')}
                        </button>
                      )}
                      <div
                        className={`modern-toggle cursor-pointer ${editingPreferences.refreshRateLocked === false ? 'checked' : ''}`}
                        onClick={() =>
                          setEditingPreferences({
                            ...editingPreferences,
                            refreshRateLocked:
                              editingPreferences.refreshRateLocked === false ? true : false
                          })
                        }
                      >
                        <span className="toggle-thumb" />
                      </div>
                    </div>
                  </div>
                </div>
              )}

              {/* Per-Service Prefill Access (Guest Users Only) */}
              {editingSession &&
                isGuestSession(editingSession) &&
                !editingSession.isRevoked &&
                !editingSession.isExpired && (
                  <div className="space-y-3">
                    <div>
                      <div className="flex items-center gap-2">
                        <Download className="w-4 h-4 text-themed-accent" />
                        <h4 className="text-sm font-medium text-themed-primary">
                          {t('activeSessions.prefill.title')}
                        </h4>
                      </div>
                      <p className="text-xs text-themed-muted mt-1">
                        {t('activeSessions.prefill.subtitle')}
                      </p>
                    </div>
                    <div className="mgmt-list divided-list">
                      {PREFILL_SERVICES.map((service: PrefillServiceConfig) => {
                        const ServiceIcon = service.icon;
                        const current = editingSession[service.sessionEnabledField];
                        const pending = pendingPrefillChanges[service.id];
                        const effective = pending !== null ? pending : current;
                        const hasChange = pending !== null && pending !== current;
                        return (
                          <div key={service.id} className="mgmt-row">
                            <div className="session-prefill-edit__label">
                              <ServiceIcon size={16} className="session-prefill-edit__icon" />
                              <span className="text-sm text-themed-secondary">
                                {service.displayName}
                              </span>
                              {hasChange && (
                                <span className="text-xs text-themed-accent italic">
                                  ({t('common.unsaved')})
                                </span>
                              )}
                            </div>
                            <div className="mgmt-row__actions">
                              <Badge variant={effective ? 'success' : 'warning'}>
                                {effective
                                  ? t('activeSessions.prefill.status.enabled')
                                  : t('activeSessions.prefill.status.disabled')}
                              </Badge>
                              <Button
                                variant="default"
                                color={effective ? 'orange' : 'green'}
                                size="sm"
                                onClick={() =>
                                  setPendingPrefillChanges((previous: PendingPrefillChanges) => ({
                                    ...previous,
                                    [service.id]: !effective
                                  }))
                                }
                              >
                                {effective
                                  ? t('activeSessions.prefill.actions.revoke')
                                  : t('activeSessions.prefill.actions.grant')}
                              </Button>
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  </div>
                )}

              {/* Max Thread Count (Guest Users Only) */}
              {editingSession &&
                isGuestSession(editingSession) &&
                (() => {
                  const threadOptions = getThreadOptions(t);
                  const threadLimitServices: ThreadLimitService[] = [
                    {
                      key: 'steamMaxThreadCount',
                      name: 'Steam',
                      defaultThreadCount: defaultGuestMaxThreadCount
                    },
                    {
                      key: 'epicMaxThreadCount',
                      name: 'Epic',
                      defaultThreadCount: epicDefaultGuestMaxThreadCount
                    }
                  ];

                  return (
                    <div>
                      <div className="flex items-center gap-1.5 mb-2">
                        <Network className="w-4 h-4 text-themed-primary" />
                        <span className="text-sm font-medium text-themed-primary">
                          {t('user.guest.prefill.maxThreads.label')}
                        </span>
                      </div>
                      <div className="space-y-3">
                        {threadLimitServices.map((service: ThreadLimitService) => {
                          const overrideThreadCount = editingPreferences[service.key] ?? null;
                          const hasOverride = overrideThreadCount != null;
                          const defaultLabel =
                            service.defaultThreadCount != null
                              ? t('user.guest.prefill.maxThreads.threadsCount', {
                                  count: service.defaultThreadCount
                                })
                              : t('user.guest.prefill.maxThreads.noLimit');

                          return (
                            <div key={service.key}>
                              <div className="flex items-center justify-between mb-1.5">
                                <span className="text-sm text-themed-secondary">
                                  {service.name}
                                </span>
                                {hasOverride ? (
                                  <button
                                    type="button"
                                    onClick={() => setThreadLimit(service.key, null)}
                                    className="text-xs px-2 py-0.5 rounded transition-colors text-themed-accent bg-themed-tertiary hover:bg-themed-secondary"
                                  >
                                    {t('actions.useDefault')}
                                  </button>
                                ) : (
                                  <Badge variant="neutral">{t('actions.usingDefault')}</Badge>
                                )}
                              </div>
                              <EnhancedDropdown
                                options={threadOptions}
                                value={
                                  hasOverride
                                    ? String(overrideThreadCount)
                                    : service.defaultThreadCount != null
                                      ? String(service.defaultThreadCount)
                                      : ''
                                }
                                onChange={(value: string) =>
                                  setThreadLimit(service.key, value === '' ? null : Number(value))
                                }
                                className="w-full"
                              />
                              <p className="text-xs text-themed-muted mt-1">
                                {hasOverride
                                  ? t('user.guest.prefill.maxThreads.overridden')
                                  : `${t('user.guest.prefill.maxThreads.usingDefault')}: ${defaultLabel}`}
                              </p>
                            </div>
                          );
                        })}
                      </div>
                    </div>
                  );
                })()}

              {/* UI Preferences */}
              <div className="space-y-3">
                <h4 className="text-sm font-medium text-themed-primary">
                  {t('activeSessions.preferencesModal.uiTitle')}
                </h4>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.sharpCorners}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        sharpCorners: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded accent-themed"
                  />
                  <span className="text-sm text-themed-secondary">
                    {t('user.guest.preferences.sharpCorners.label')}
                  </span>
                </label>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={!editingPreferences.disableTooltips}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        disableTooltips: !e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded accent-themed"
                  />
                  <span className="text-sm text-themed-secondary">
                    {t('activeSessions.preferencesModal.tooltips')}
                  </span>
                </label>

                {editingSession && isAdminSession(editingSession) && (
                  <>
                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={!editingPreferences.disableStickyNotifications}
                        onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            disableStickyNotifications: !e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded accent-themed"
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">
                          {t('activeSessions.preferencesModal.stickyNotifications.title')}
                        </span>
                        <span className="text-xs text-themed-muted">
                          {t('activeSessions.preferencesModal.stickyNotifications.description')}
                        </span>
                      </div>
                    </label>

                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={editingPreferences.picsAlwaysVisible}
                        onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            picsAlwaysVisible: e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded accent-themed"
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">
                          {t('activeSessions.preferencesModal.staticNotifications.title')}
                        </span>
                        <span className="text-xs text-themed-muted">
                          {t('activeSessions.preferencesModal.staticNotifications.description')}
                        </span>
                      </div>
                    </label>
                  </>
                )}

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.showDatasourceLabels}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        showDatasourceLabels: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded accent-themed"
                  />
                  <div className="flex flex-col">
                    <span className="text-sm text-themed-secondary">
                      {t('user.guest.preferences.datasourceLabels.label')}
                    </span>
                    <span className="text-xs text-themed-muted">
                      {t('activeSessions.preferencesModal.datasourceLabels')}
                    </span>
                  </div>
                </label>
              </div>

              {/* Date & Time Preferences */}
              <div className="space-y-3">
                <h4 className="text-sm font-medium text-themed-primary">
                  {t('user.guest.sections.dateTime')}
                </h4>

                {/* Allowed Time Formats Multi-Select */}
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <label className="text-sm text-themed-secondary">
                      {t('user.guest.timeFormats.title')}
                    </label>
                    {(() => {
                      const currentFormats = editingPreferences.allowedTimeFormats;
                      const defaultFormats: string[] = defaultGuestPrefs.allowedTimeFormats ?? [
                        ...TIME_SETTING_VALUES
                      ];
                      const isUsingDefault =
                        !currentFormats ||
                        (currentFormats.length === defaultFormats.length &&
                          currentFormats.every((f: string) => defaultFormats.includes(f)));

                      return isUsingDefault ? (
                        <Badge variant="neutral">{t('actions.usingDefault')}</Badge>
                      ) : (
                        <button
                          type="button"
                          onClick={() =>
                            setEditingPreferences({
                              ...editingPreferences,
                              allowedTimeFormats: undefined
                            })
                          }
                          className="text-xs px-2 py-0.5 rounded transition-colors text-themed-accent bg-themed-tertiary hover:bg-themed-secondary"
                        >
                          {t('actions.useDefault')}
                        </button>
                      );
                    })()}
                  </div>
                  <MultiSelectDropdown
                    options={timeFormatOptions}
                    values={
                      editingPreferences.allowedTimeFormats ??
                      defaultGuestPrefs.allowedTimeFormats ?? [...TIME_SETTING_VALUES]
                    }
                    onChange={(formats: string[]) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        allowedTimeFormats: formats
                      })
                    }
                    placeholder={t('user.guest.timeFormats.placeholder')}
                    minSelections={1}
                    dropdownWidth="w-80"
                  />
                  <p className="text-xs text-themed-muted mt-1">
                    {editingPreferences.allowedTimeFormats
                      ? t('activeSessions.preferencesModal.customFormats')
                      : t('activeSessions.preferencesModal.defaultFormats', {
                          formats:
                            defaultGuestPrefs.allowedTimeFormats?.length === 4
                              ? t('activeSessions.preferencesModal.allFormats')
                              : defaultGuestPrefs.allowedTimeFormats
                                  ?.map(
                                    (f: string) =>
                                      timeFormatOptions.find(
                                        (o: { value: string; label: string }) => o.value === f
                                      )?.label
                                  )
                                  .join(', ') || t('activeSessions.preferencesModal.allFormats')
                        })}
                  </p>
                </div>
              </div>
            </div>
          )}

          <div className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary">
            <Button
              variant="default"
              onClick={() => {
                setEditingSession(null);
                setEditingPreferences(null);
                setPendingPrefillChanges(NO_PENDING_PREFILL_CHANGES);
              }}
              disabled={savingPreferences}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="blue"
              onClick={handleSavePreferences}
              loading={savingPreferences}
              disabled={loadingPreferences}
            >
              {t('activeSessions.preferencesModal.save')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default ActiveSessions;
