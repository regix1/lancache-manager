import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Users,
  Trash2,
  AlertTriangle,
  Network,
  Globe,
  MapPin,
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
import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection, HelpDefinition } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { Pagination } from '@components/ui/Pagination';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import Badge from '@components/ui/Badge';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { ActionMenuItem, ActionMenuDivider, ActionMenuDangerItem } from '@components/ui/ActionMenu';
import { EmptyState } from '@components/ui/ManagerCard';
import '../management/managementSectionContent.css';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  EpicGuestPrefillConfigChangedEvent,
  XboxGuestPrefillConfigChangedEvent
} from '@contexts/SignalRContext/types';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useActivityTracker } from '@hooks/useActivityTracker';
import { useClientInfoReporter } from '@hooks/useClientInfoReporter';
import { storage } from '@utils/storage';
import {
  type Session,
  type SessionFilter,
  type UserPreferences,
  type ThemeOption,
  refreshRateOptions,
  cleanIpAddress,
  countryCodeToFlag,
  formatLocation,
  showToast,
  parseUserAgent
} from './types';

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

const FormattedTimestamp: React.FC<{ timestamp: string }> = ({ timestamp }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return <>{formattedTime}</>;
};

// ============================================================
// Pure Helper Functions
// ============================================================

const isAdminSession = (session: Session): boolean => {
  return session.sessionType === 'admin';
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
  const { on, off } = useSignalR();
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
  const { isActive: isLocallyActive } = useActivityTracker();
  useClientInfoReporter(authService.isAuthenticated, authService.sessionId);
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
  const [pendingSteamPrefillChange, setPendingSteamPrefillChange] = useState<boolean | null>(null);
  const [pendingEpicPrefillChange, setPendingEpicPrefillChange] = useState<boolean | null>(null);
  const [pendingBattlenetPrefillChange, setPendingBattlenetPrefillChange] = useState<
    boolean | null
  >(null);
  const [pendingRiotPrefillChange, setPendingRiotPrefillChange] = useState<boolean | null>(null);
  const [pendingXboxPrefillChange, setPendingXboxPrefillChange] = useState<boolean | null>(null);
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
  const timeFormatOptions = [
    {
      value: 'server-24h',
      label: t('user.guest.timeFormats.server24h.label'),
      description: t('user.guest.timeFormats.server24h.description'),
      icon: Globe
    },
    {
      value: 'server-12h',
      label: t('user.guest.timeFormats.server12h.label'),
      description: t('user.guest.timeFormats.server12h.description'),
      icon: Globe
    },
    {
      value: 'local-24h',
      label: t('user.guest.timeFormats.local24h.label'),
      description: t('user.guest.timeFormats.local24h.description'),
      icon: MapPin
    },
    {
      value: 'local-12h',
      label: t('user.guest.timeFormats.local12h.label'),
      description: t('user.guest.timeFormats.local12h.description'),
      icon: MapPin
    }
  ];

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
        let loadedSessions = first.sessions;
        const serverPages = first.pagination?.totalPages || 1;
        if (serverPages > 1) {
          const rest = await Promise.all(
            Array.from({ length: serverPages - 1 }, (_: unknown, i: number) =>
              ApiService.getSessions<SessionsResponse>(i + 2, 100)
            )
          );
          loadedSessions = loadedSessions.concat(rest.flatMap((r: SessionsResponse) => r.sessions));
        }
        setSessions(loadedSessions);
        setHistorySessions(first.historySessions);
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
    setPendingSteamPrefillChange(null);
    setPendingEpicPrefillChange(null);
    setPendingBattlenetPrefillChange(null);
    setPendingRiotPrefillChange(null);
    setPendingXboxPrefillChange(null);
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
        use24HourFormat: prefs.use24HourFormat,
        showYearInDates: prefs.showYearInDates,
        refreshRate: prefs.refreshRate ?? null,
        refreshRateLocked: prefs.refreshRateLocked ?? null,
        allowedTimeFormats: prefs.allowedTimeFormats,
        maxThreadCount: prefs.maxThreadCount ?? null
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

        const prefillToggles: { service: string; enabled: boolean }[] = [];
        if (pendingSteamPrefillChange !== null) {
          prefillToggles.push({ service: 'steam', enabled: pendingSteamPrefillChange });
        }
        if (pendingEpicPrefillChange !== null) {
          prefillToggles.push({ service: 'epic', enabled: pendingEpicPrefillChange });
        }
        if (pendingBattlenetPrefillChange !== null) {
          prefillToggles.push({ service: 'battlenet', enabled: pendingBattlenetPrefillChange });
        }
        if (pendingRiotPrefillChange !== null) {
          prefillToggles.push({ service: 'riot', enabled: pendingRiotPrefillChange });
        }
        if (pendingXboxPrefillChange !== null) {
          prefillToggles.push({ service: 'xbox', enabled: pendingXboxPrefillChange });
        }
        await Promise.all(
          prefillToggles.map(({ service, enabled }: { service: string; enabled: boolean }) =>
            ApiService.toggleGuestPrefillService(editingSession.id, service, enabled)
          )
        );
      }

      setEditingSession(null);
      setEditingPreferences(null);
      setPendingSteamPrefillChange(null);
      setPendingEpicPrefillChange(null);
      setPendingBattlenetPrefillChange(null);
      setPendingRiotPrefillChange(null);
      setPendingXboxPrefillChange(null);
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
      setSessions((prev: Session[]) =>
        prev.map((s: Session) => {
          if (s.id !== data.sessionId) return s;
          if (data.service === 'epic') {
            return {
              ...s,
              epicPrefillEnabled: data.enabled,
              epicPrefillExpiresAt: data.prefillExpiresAt || null
            };
          } else if (data.service === 'battlenet') {
            return {
              ...s,
              battlenetPrefillEnabled: data.enabled,
              battlenetPrefillExpiresAt: data.prefillExpiresAt || null
            };
          } else if (data.service === 'riot') {
            return {
              ...s,
              riotPrefillEnabled: data.enabled,
              riotPrefillExpiresAt: data.prefillExpiresAt || null
            };
          } else if (data.service === 'xbox') {
            return {
              ...s,
              xboxPrefillEnabled: data.enabled,
              xboxPrefillExpiresAt: data.prefillExpiresAt || null
            };
          } else {
            return {
              ...s,
              steamPrefillEnabled: data.enabled,
              steamPrefillExpiresAt: data.prefillExpiresAt || null
            };
          }
        })
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
    const expiryStr =
      expiresAt.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(expiresAt) ? expiresAt : expiresAt + 'Z';
    const expiry = new Date(expiryStr);
    const diff = expiry.getTime() - now.getTime();

    if (diff <= 0) return t('activeSessions.prefill.status.expired');

    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));

    if (hours > 0) {
      return t(
        'activeSessions.prefill.status.hoursMinutesRemaining',
        '{{hours}}h {{minutes}}m remaining',
        {
          hours,
          minutes
        }
      );
    }
    return t('activeSessions.prefill.status.minutesRemaining', '{{minutes}}m remaining', {
      minutes
    });
  };

  // i18n relative time for the row's last-seen meta (module getRelativeTime is
  // English-only; this threads t so the copy is translatable).
  const formatRelativeTime = (dateString: string | null): string => {
    if (!dateString) return t('activeSessions.relative.never', 'Never');
    const now = new Date();
    const rawStr =
      dateString.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(dateString)
        ? dateString
        : dateString + 'Z';
    const date = new Date(rawStr);
    const diffSecs = Math.floor((now.getTime() - date.getTime()) / 1000);
    if (diffSecs < 60) return t('activeSessions.relative.justNow', 'Just now');
    const diffMins = Math.floor(diffSecs / 60);
    if (diffMins < 60)
      return t('activeSessions.relative.minutesAgo', '{{count}}m ago', { count: diffMins });
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24)
      return t('activeSessions.relative.hoursAgo', '{{count}}h ago', { count: diffHours });
    const diffDays = Math.floor(diffHours / 24);
    return t('activeSessions.relative.daysAgo', '{{count}}d ago', { count: diffDays });
  };

  type SessionStatus = 'active' | 'away' | 'inactive';

  const getSessionStatus = (session: Session): SessionStatus => {
    if (session.isRevoked || session.isExpired) return 'inactive';

    if (session.isCurrentSession && isLocallyActive) {
      return 'active';
    }

    if (!session.lastSeenAt) return 'inactive';

    const now = new Date();
    const lastSeenStr =
      session.lastSeenAt.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(session.lastSeenAt)
        ? session.lastSeenAt
        : session.lastSeenAt + 'Z';
    const lastSeen = new Date(lastSeenStr);
    const diffSeconds = (now.getTime() - lastSeen.getTime()) / 1000;

    if (diffSeconds <= 60) return 'active';
    if (diffSeconds <= 600) return 'away';
    return 'inactive';
  };

  const getCountForFilter = (filter: SessionFilter): number => {
    if (filter === 'all') return activeSessions.length;
    if (filter === 'admin') return activeSessions.filter((s: Session) => isAdminSession(s)).length;
    return activeSessions.filter((s: Session) => isGuestSession(s)).length;
  };

  const getFilterLabel = (filter: SessionFilter): string => {
    if (filter === 'all') return t('activeSessions.filters.all', 'All');
    if (filter === 'admin') return t('activeSessions.filters.admin', 'Admin');
    return t('activeSessions.filters.guest', 'Guest');
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
      : t('activeSessions.preferencesModal.defaultThemeShort', 'Default');
    const timezoneLabel = prefs?.useLocalTimezone
      ? t('activeSessions.labels.local', 'Local')
      : t('activeSessions.labels.server', 'Server');

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
          <span className={`status-dot ${sessionStatus}`} aria-hidden="true" />
          <div className="mgmt-row__body">
            <div className="session-row__titleline">
              <span className="mgmt-row__title truncate">{parsedUA.title}</span>
              <span
                className={`themed-badge session-type-badge ${admin ? 'session-badge-user' : 'session-badge-guest'}`}
              >
                {admin
                  ? t('activeSessions.labels.userBadge')
                  : t('activeSessions.labels.guestBadge')}
              </span>
              {session.isCurrentSession && (
                <span className="session-you">
                  ({t('activeSessions.currentSessionShort', 'you')})
                </span>
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
            <Button
              variant="default"
              color="blue"
              size="sm"
              onClick={() => handleEditSession(session)}
            >
              {t('actions.edit')}
            </Button>
            {session.isCurrentSession && (
              <Button
                variant="default"
                color="orange"
                size="sm"
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
                size="sm"
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
                size="sm"
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
                      {t('activeSessions.labels.publicIp', 'Public IP')}
                    </span>
                    <span className="mgmt-stat__value">{session.publicIpAddress}</span>
                  </div>
                )}
                {location && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.location', 'Location')}
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
                      {t('activeSessions.labels.isp', 'ISP')}
                    </span>
                    <span className="mgmt-stat__value">{session.ispName}</span>
                  </div>
                )}
                {session.timezone && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.timezoneHeading', 'Timezone')}
                    </span>
                    <span className="mgmt-stat__value">{session.timezone}</span>
                  </div>
                )}
                {session.browserLanguage && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.language', 'Language')}
                    </span>
                    <span className="mgmt-stat__value">{session.browserLanguage}</span>
                  </div>
                )}
                {session.screenResolution && (
                  <div className="mgmt-stat">
                    <span className="mgmt-stat__label caps-label caps-label--sm">
                      {t('activeSessions.labels.screen', 'Screen')}
                    </span>
                    <span className="mgmt-stat__value">{session.screenResolution}</span>
                  </div>
                )}
              </div>
            )}

            {!session.isRevoked && !session.isExpired && (
              <div className="space-y-2">
                <p className="mgmt-subhead caps-label">
                  {t('activeSessions.labels.preferences', 'Preferences')}
                </p>
                {isLoadingPrefs ? (
                  <div className="flex items-center gap-2 text-xs text-themed-muted">
                    <LoadingSpinner inline size="xs" />
                    {t('activeSessions.preferencesModal.loading', 'Loading preferences...')}
                  </div>
                ) : prefs ? (
                  <div className="mgmt-stat-grid">
                    <div className="mgmt-stat">
                      <span className="mgmt-stat__label caps-label caps-label--sm">
                        {t('activeSessions.labels.theme', 'Theme')}
                      </span>
                      <span className="mgmt-stat__value">{themeName}</span>
                    </div>
                    <div className="mgmt-stat">
                      <span className="mgmt-stat__label caps-label caps-label--sm">
                        {t('activeSessions.labels.timezoneHeading', 'Timezone')}
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
                <p className="mgmt-subhead caps-label">
                  {t('activeSessions.prefill.title', 'Prefill Access')}
                </p>
                <div className="session-prefill-readout">
                  <span
                    className={`session-prefill-svc ${session.steamPrefillEnabled ? 'is-enabled' : 'is-disabled'}`}
                  >
                    <SteamIcon size={12} />
                    Steam
                  </span>
                  <span
                    className={`session-prefill-svc ${session.epicPrefillEnabled ? 'is-enabled' : 'is-disabled'}`}
                  >
                    <EpicIcon size={12} />
                    Epic
                  </span>
                  <span
                    className={`session-prefill-svc ${session.battlenetPrefillEnabled ? 'is-enabled' : 'is-disabled'}`}
                  >
                    <BlizzardIcon size={12} />
                    Battle.net
                  </span>
                  <span
                    className={`session-prefill-svc ${session.riotPrefillEnabled ? 'is-enabled' : 'is-disabled'}`}
                  >
                    <RiotIcon size={12} />
                    Riot
                  </span>
                  <span
                    className={`session-prefill-svc ${session.xboxPrefillEnabled ? 'is-enabled' : 'is-disabled'}`}
                  >
                    <XboxIcon size={12} />
                    Xbox
                  </span>
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
                  {t('activeSessions.labels.createdShort', 'Created')}
                </span>
              </div>
              <div className="dash-readout-item">
                <span className="dash-readout-value">
                  {session.lastSeenAt ? (
                    <FormattedTimestamp timestamp={session.lastSeenAt} />
                  ) : (
                    t('activeSessions.labels.never', 'Never')
                  )}
                </span>
                <span className="caps-label caps-label--wide dash-readout-label">
                  {t('activeSessions.labels.lastSeenShort', 'Last Seen')}
                </span>
              </div>
              <div className="dash-readout-item">
                <span className="dash-readout-value">
                  {admin ? (
                    t('activeSessions.labels.never', 'Never')
                  ) : (
                    <FormattedTimestamp timestamp={session.expiresAt} />
                  )}
                </span>
                <span className="caps-label caps-label--wide dash-readout-label">
                  {t('activeSessions.labels.expires', 'Expires')}
                </span>
              </div>
              {session.revokedAt && (
                <div className="dash-readout-item">
                  <span className="dash-readout-value is-error">
                    <FormattedTimestamp timestamp={session.revokedAt} />
                  </span>
                  <span className="caps-label caps-label--wide dash-readout-label">
                    {t('activeSessions.labels.revokedShort', 'Revoked')}
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
            <span className="mgmt-row__title truncate">{parsedUA.title}</span>
            <span
              className={`themed-badge session-type-badge ${admin ? 'session-badge-user' : 'session-badge-guest'}`}
            >
              {admin ? t('activeSessions.labels.userBadge') : t('activeSessions.labels.guestBadge')}
            </span>
            {session.isRevoked && (
              <span className="themed-badge status-badge-error session-type-badge">
                {t('activeSessions.status.revoked')}
              </span>
            )}
            {session.isExpired && !session.isRevoked && (
              <span className="themed-badge status-badge-warning session-type-badge">
                {t('activeSessions.prefill.status.expired')}
              </span>
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
        <div className="flex items-center gap-2 mb-3 sm:mb-4">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-blue)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('user.groups.sessions')}
          </h3>
        </div>

        <div className="space-y-4">
          <AccordionSection
            title={t('activeSessions.title')}
            description={t('activeSessions.summary')}
            titleAccessory={
              <HelpPopover width={320}>
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
              <div className="flex flex-wrap items-center gap-2 w-full justify-start sm:w-auto sm:justify-end">
                <Badge variant={guestModeLocked ? 'error' : 'success'}>
                  {guestModeLocked
                    ? t('activeSessions.toggle.locked')
                    : t('activeSessions.toggle.unlocked')}
                </Badge>
                <SectionActionsMenu
                  label={t('management.actions.menuLabel', 'Actions')}
                  width="w-56"
                >
                  {(close) => (
                    <>
                      <ActionMenuItem
                        icon={<RotateCcw className="w-4 h-4" />}
                        onClick={() => {
                          close();
                          setShowBulkResetConfirm(true);
                        }}
                      >
                        {t('user.bulkActions.buttons.reset', 'Reset All to Defaults')}
                      </ActionMenuItem>
                      <ActionMenuDivider />
                      <ActionMenuDangerItem
                        icon={<Eraser className="w-4 h-4" />}
                        onClick={() => {
                          close();
                          setShowClearGuestsConfirm(true);
                        }}
                      >
                        {t('user.bulkActions.buttons.clear', 'Clear All Guest Sessions')}
                      </ActionMenuDangerItem>
                    </>
                  )}
                </SectionActionsMenu>
              </div>
            }
          >
            <div className="space-y-4">
              {/* Guest lock control stays visible (not buried in kebab); filters stay in-body */}
              <div className="mgmt-toolbar session-toolbar">
                {!loading && activeSessions.length > 0 ? (
                  <div className="session-filter-cluster">
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

                    <div className="relative min-w-[19rem] max-w-md flex-1">
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
                      className="min-w-[6.5rem]"
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
                <div className="text-center py-12">
                  <LoadingSpinner inline size="xl" className="mx-auto text-themed-accent" />
                  <p className="text-sm mt-3 text-themed-muted">{t('activeSessions.loading')}</p>
                </div>
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
                  title={t('activeSessions.empty.filteredTitle', 'No matching sessions')}
                  subtitle={t(
                    'activeSessions.empty.filtered',
                    'No sessions match the selected filter.'
                  )}
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
              description={t('activeSessions.history.summary')}
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
      <Modal
        opened={!!pendingRevokeSession}
        onClose={() => {
          if (!revokingSession) {
            setPendingRevokeSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('activeSessions.revokeModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
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
            <div>
              <p className="text-sm font-medium mb-2">
                {t('activeSessions.revokeModal.noteTitle')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('activeSessions.revokeModal.points.marked')}</li>
                <li>{t('activeSessions.revokeModal.points.logout')}</li>
                <li>{t('activeSessions.revokeModal.points.history')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingRevokeSession(null)}
              disabled={!!revokingSession}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={confirmRevokeSession}
              loading={!!revokingSession}
            >
              {t('activeSessions.revokeModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Session Modal */}
      <Modal
        opened={!!pendingDeleteSession}
        onClose={() => {
          if (!deletingSession) {
            setPendingDeleteSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Trash2 className="w-6 h-6 text-themed-error" />
            <span>{t('activeSessions.deleteModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
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
            <div>
              <p className="text-sm font-medium mb-2">
                {t('activeSessions.deleteModal.noteTitle')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('activeSessions.deleteModal.points.noUndo')}</li>
                <li>{t('activeSessions.deleteModal.points.removed')}</li>
                <li>{t('activeSessions.deleteModal.points.logout')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingDeleteSession(null)}
              disabled={!!deletingSession}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={confirmDeleteSession}
              loading={!!deletingSession}
            >
              {t('activeSessions.deleteModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Bulk Reset Confirmation Modal */}
      <Modal
        opened={showBulkResetConfirm}
        onClose={() => {
          if (!bulkActionInProgress) {
            setShowBulkResetConfirm(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <RotateCcw className="w-6 h-6 text-themed-warning" />
            <span>{t('user.bulkActions.resetModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">{t('user.bulkActions.resetModal.message')}</p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('user.bulkActions.resetModal.noteTitle')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('user.bulkActions.resetModal.points.theme')}</li>
                <li>{t('user.bulkActions.resetModal.points.refreshRate')}</li>
                <li>{t('user.bulkActions.resetModal.points.preferences')}</li>
                <li>{t('user.bulkActions.resetModal.points.active')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowBulkResetConfirm(false)}
              disabled={!!bulkActionInProgress}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={handleBulkResetToDefaults}
              loading={bulkActionInProgress === 'reset'}
            >
              {t('user.bulkActions.resetModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Clear All Guests Confirmation Modal */}
      <Modal
        opened={showClearGuestsConfirm}
        onClose={() => {
          if (!bulkActionInProgress) {
            setShowClearGuestsConfirm(false);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Eraser className="w-6 h-6 text-themed-error" />
            <span>{t('user.bulkActions.clearModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">{t('user.bulkActions.clearModal.message')}</p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">
                {t('user.bulkActions.clearModal.noteTitle')}
              </p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('user.bulkActions.clearModal.points.deleted')}</li>
                <li>{t('user.bulkActions.clearModal.points.logout')}</li>
                <li>{t('user.bulkActions.clearModal.points.data')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setShowClearGuestsConfirm(false)}
              disabled={!!bulkActionInProgress}
            >
              {t('actions.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={handleClearAllGuests}
              loading={bulkActionInProgress === 'clear'}
            >
              {t('user.bulkActions.clearModal.confirm')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Edit User Preferences Modal */}
      <Modal
        opened={!!editingSession}
        onClose={() => {
          if (!savingPreferences) {
            setEditingSession(null);
            setEditingPreferences(null);
            setPendingSteamPrefillChange(null);
            setPendingEpicPrefillChange(null);
            setPendingBattlenetPrefillChange(null);
            setPendingRiotPrefillChange(null);
            setPendingXboxPrefillChange(null);
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
            <div className="text-center py-8">
              <LoadingSpinner inline size="xl" className="mx-auto text-themed-muted" />
              <p className="text-sm mt-2 text-themed-secondary">
                {t('activeSessions.preferencesModal.loading')}
              </p>
            </div>
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
                    <span className="text-xs px-2 py-0.5 rounded text-themed-muted bg-themed-tertiary">
                      {t('actions.usingDefault')}
                    </span>
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
                      <span className="text-xs px-2 py-0.5 rounded text-themed-muted bg-themed-tertiary">
                        {t('actions.usingDefault')}
                      </span>
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
                          {t(
                            'activeSessions.preferencesModal.refreshLock.allow',
                            'Allow guest to change rate'
                          )}
                        </p>
                        <p className="text-xs text-themed-muted">
                          {editingPreferences.refreshRateLocked === null
                            ? t(
                                'activeSessions.preferencesModal.refreshLock.usingDefault',
                                'Using global default'
                              )
                            : editingPreferences.refreshRateLocked
                              ? t(
                                  'activeSessions.preferencesModal.refreshLock.locked',
                                  'Locked for this guest'
                                )
                              : t(
                                  'activeSessions.preferencesModal.refreshLock.unlocked',
                                  'Unlocked for this guest'
                                )}
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
                      {/* Steam Prefill Row */}
                      {(() => {
                        const current = editingSession.steamPrefillEnabled;
                        const effective =
                          pendingSteamPrefillChange !== null ? pendingSteamPrefillChange : current;
                        const hasChange =
                          pendingSteamPrefillChange !== null &&
                          pendingSteamPrefillChange !== current;
                        return (
                          <div className="mgmt-row">
                            <div className="session-prefill-edit__label">
                              <SteamIcon size={16} className="session-prefill-edit__icon" />
                              <span className="text-sm text-themed-secondary">Steam</span>
                              {hasChange && (
                                <span className="text-xs text-themed-accent italic">
                                  ({t('common.unsaved')})
                                </span>
                              )}
                            </div>
                            <div className="mgmt-row__actions">
                              <span
                                className={`px-2 py-0.5 text-xs font-medium themed-badge ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
                              >
                                {effective
                                  ? t('activeSessions.prefill.status.enabled')
                                  : t('activeSessions.prefill.status.disabled')}
                              </span>
                              <Button
                                variant="default"
                                color={effective ? 'orange' : 'green'}
                                size="sm"
                                onClick={() => setPendingSteamPrefillChange(!effective)}
                              >
                                {effective
                                  ? t('activeSessions.prefill.actions.revoke')
                                  : t('activeSessions.prefill.actions.grant')}
                              </Button>
                            </div>
                          </div>
                        );
                      })()}

                      {/* Epic Prefill Row */}
                      {(() => {
                        const current = editingSession.epicPrefillEnabled;
                        const effective =
                          pendingEpicPrefillChange !== null ? pendingEpicPrefillChange : current;
                        const hasChange =
                          pendingEpicPrefillChange !== null && pendingEpicPrefillChange !== current;
                        return (
                          <div className="mgmt-row">
                            <div className="session-prefill-edit__label">
                              <EpicIcon size={16} className="session-prefill-edit__icon" />
                              <span className="text-sm text-themed-secondary">Epic Games</span>
                              {hasChange && (
                                <span className="text-xs text-themed-accent italic">
                                  ({t('common.unsaved')})
                                </span>
                              )}
                            </div>
                            <div className="mgmt-row__actions">
                              <span
                                className={`px-2 py-0.5 text-xs font-medium themed-badge ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
                              >
                                {effective
                                  ? t('activeSessions.prefill.status.enabled')
                                  : t('activeSessions.prefill.status.disabled')}
                              </span>
                              <Button
                                variant="default"
                                color={effective ? 'orange' : 'green'}
                                size="sm"
                                onClick={() => setPendingEpicPrefillChange(!effective)}
                              >
                                {effective
                                  ? t('activeSessions.prefill.actions.revoke')
                                  : t('activeSessions.prefill.actions.grant')}
                              </Button>
                            </div>
                          </div>
                        );
                      })()}

                      {/* Battle.net Prefill Row (anonymous - no account login) */}
                      {(() => {
                        const current = editingSession.battlenetPrefillEnabled;
                        const effective =
                          pendingBattlenetPrefillChange !== null
                            ? pendingBattlenetPrefillChange
                            : current;
                        const hasChange =
                          pendingBattlenetPrefillChange !== null &&
                          pendingBattlenetPrefillChange !== current;
                        return (
                          <div className="mgmt-row">
                            <div className="session-prefill-edit__label">
                              <BlizzardIcon size={16} className="session-prefill-edit__icon" />
                              <span className="text-sm text-themed-secondary">Battle.net</span>
                              {hasChange && (
                                <span className="text-xs text-themed-accent italic">
                                  ({t('common.unsaved')})
                                </span>
                              )}
                            </div>
                            <div className="mgmt-row__actions">
                              <span
                                className={`px-2 py-0.5 text-xs font-medium themed-badge ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
                              >
                                {effective
                                  ? t('activeSessions.prefill.status.enabled')
                                  : t('activeSessions.prefill.status.disabled')}
                              </span>
                              <Button
                                variant="default"
                                color={effective ? 'orange' : 'green'}
                                size="sm"
                                onClick={() => setPendingBattlenetPrefillChange(!effective)}
                              >
                                {effective
                                  ? t('activeSessions.prefill.actions.revoke')
                                  : t('activeSessions.prefill.actions.grant')}
                              </Button>
                            </div>
                          </div>
                        );
                      })()}

                      {/* Riot Prefill Row (anonymous - no account login) */}
                      {(() => {
                        const current = editingSession.riotPrefillEnabled;
                        const effective =
                          pendingRiotPrefillChange !== null ? pendingRiotPrefillChange : current;
                        const hasChange =
                          pendingRiotPrefillChange !== null && pendingRiotPrefillChange !== current;
                        return (
                          <div className="mgmt-row">
                            <div className="session-prefill-edit__label">
                              <RiotIcon size={16} className="session-prefill-edit__icon" />
                              <span className="text-sm text-themed-secondary">Riot Games</span>
                              {hasChange && (
                                <span className="text-xs text-themed-accent italic">
                                  ({t('common.unsaved')})
                                </span>
                              )}
                            </div>
                            <div className="mgmt-row__actions">
                              <span
                                className={`px-2 py-0.5 text-xs font-medium themed-badge ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
                              >
                                {effective
                                  ? t('activeSessions.prefill.status.enabled')
                                  : t('activeSessions.prefill.status.disabled')}
                              </span>
                              <Button
                                variant="default"
                                color={effective ? 'orange' : 'green'}
                                size="sm"
                                onClick={() => setPendingRiotPrefillChange(!effective)}
                              >
                                {effective
                                  ? t('activeSessions.prefill.actions.revoke')
                                  : t('activeSessions.prefill.actions.grant')}
                              </Button>
                            </div>
                          </div>
                        );
                      })()}

                      {/* Xbox Prefill Row (login-required - mirrors Epic) */}
                      {(() => {
                        const current = editingSession.xboxPrefillEnabled;
                        const effective =
                          pendingXboxPrefillChange !== null ? pendingXboxPrefillChange : current;
                        const hasChange =
                          pendingXboxPrefillChange !== null && pendingXboxPrefillChange !== current;
                        return (
                          <div className="mgmt-row">
                            <div className="session-prefill-edit__label">
                              <XboxIcon size={16} className="session-prefill-edit__icon" />
                              <span className="text-sm text-themed-secondary">Xbox</span>
                              {hasChange && (
                                <span className="text-xs text-themed-accent italic">
                                  ({t('common.unsaved')})
                                </span>
                              )}
                            </div>
                            <div className="mgmt-row__actions">
                              <span
                                className={`px-2 py-0.5 text-xs font-medium themed-badge ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
                              >
                                {effective
                                  ? t('activeSessions.prefill.status.enabled')
                                  : t('activeSessions.prefill.status.disabled')}
                              </span>
                              <Button
                                variant="default"
                                color={effective ? 'orange' : 'green'}
                                size="sm"
                                onClick={() => setPendingXboxPrefillChange(!effective)}
                              >
                                {effective
                                  ? t('activeSessions.prefill.actions.revoke')
                                  : t('activeSessions.prefill.actions.grant')}
                              </Button>
                            </div>
                          </div>
                        );
                      })()}
                    </div>
                  </div>
                )}

              {/* Max Thread Count (Guest Users Only) */}
              {editingSession &&
                isGuestSession(editingSession) &&
                (() => {
                  const THREAD_VALUES = [1, 2, 4, 8, 16, 32, 64, 128, 256];
                  const threadOptions = [
                    { value: '', label: t('user.guest.prefill.maxThreads.noLimit') },
                    ...THREAD_VALUES.map((n: number) => ({
                      value: String(n),
                      label: t('user.guest.prefill.maxThreads.threadsCount', '{{count}} threads', {
                        count: n
                      })
                    }))
                  ];
                  const hasOverride = editingPreferences.maxThreadCount != null;

                  return (
                    <div>
                      <div className="flex items-center justify-between mb-2">
                        <label className="text-sm font-medium text-themed-primary flex items-center gap-1.5">
                          <Network className="w-4 h-4" />
                          {t('user.guest.prefill.maxThreads.label')}
                        </label>
                        {hasOverride ? (
                          <button
                            type="button"
                            onClick={() =>
                              setEditingPreferences({
                                ...editingPreferences,
                                maxThreadCount: null
                              })
                            }
                            className="text-xs px-2 py-0.5 rounded transition-colors text-themed-accent bg-themed-tertiary hover:bg-themed-secondary"
                          >
                            {t('actions.useDefault')}
                          </button>
                        ) : (
                          <span className="text-xs px-2 py-0.5 rounded text-themed-muted bg-themed-tertiary">
                            {t('actions.usingDefault')}
                          </span>
                        )}
                      </div>
                      <EnhancedDropdown
                        options={threadOptions}
                        value={
                          editingPreferences.maxThreadCount != null
                            ? String(editingPreferences.maxThreadCount)
                            : defaultGuestMaxThreadCount != null
                              ? String(defaultGuestMaxThreadCount)
                              : ''
                        }
                        onChange={(value: string) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            maxThreadCount: value === '' ? null : Number(value)
                          })
                        }
                        className="w-full"
                      />
                      <p className="text-xs text-themed-muted mt-1">
                        {hasOverride
                          ? t('user.guest.prefill.maxThreads.overridden')
                          : defaultGuestMaxThreadCount != null
                            ? `${t('user.guest.prefill.maxThreads.usingDefault')}: ${defaultGuestMaxThreadCount} threads (Steam)`
                            : `${t('user.guest.prefill.maxThreads.usingDefault')}: ${t('user.guest.prefill.maxThreads.noLimit')} (Steam)`}
                      </p>
                      {epicDefaultGuestMaxThreadCount !== null && (
                        <p className="text-xs text-themed-muted mt-0.5">
                          {`${t('user.guest.prefill.maxThreads.usingDefault')}: ${epicDefaultGuestMaxThreadCount} threads (Epic)`}
                        </p>
                      )}
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
                      const defaultFormats = defaultGuestPrefs.allowedTimeFormats ?? [
                        'server-24h',
                        'server-12h',
                        'local-24h',
                        'local-12h'
                      ];
                      const isUsingDefault =
                        !currentFormats ||
                        (currentFormats.length === defaultFormats.length &&
                          currentFormats.every((f: string) => defaultFormats.includes(f)));

                      return isUsingDefault ? (
                        <span className="text-xs px-2 py-0.5 rounded text-themed-muted bg-themed-tertiary">
                          {t('actions.usingDefault')}
                        </span>
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
                    options={timeFormatOptions.map(
                      (opt: {
                        value: string;
                        label: string;
                        description: string;
                        icon: typeof Globe;
                      }) => ({
                        value: opt.value,
                        label: opt.label,
                        description: opt.description,
                        icon: opt.icon
                      })
                    )}
                    values={
                      editingPreferences.allowedTimeFormats ??
                      defaultGuestPrefs.allowedTimeFormats ?? [
                        'server-24h',
                        'server-12h',
                        'local-24h',
                        'local-12h'
                      ]
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

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.showYearInDates}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        showYearInDates: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded accent-themed"
                  />
                  <div className="flex flex-col">
                    <span className="text-sm text-themed-secondary">
                      {t('user.guest.preferences.showYear.label')}
                    </span>
                    <span className="text-xs text-themed-muted">
                      {t('user.guest.preferences.showYear.description')}
                    </span>
                  </div>
                </label>
              </div>
            </div>
          )}

          <div className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary">
            <Button
              variant="default"
              onClick={() => {
                setEditingSession(null);
                setEditingPreferences(null);
                setPendingSteamPrefillChange(null);
                setPendingEpicPrefillChange(null);
                setPendingBattlenetPrefillChange(null);
                setPendingRiotPrefillChange(null);
                setPendingXboxPrefillChange(null);
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
