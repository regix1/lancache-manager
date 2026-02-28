import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Users,
  User,
  Trash2,
  Loader2,
  AlertTriangle,
  Clock,
  Network,
  Globe,
  MapPin,
  Edit,
  Lock,
  Unlock,
  ChevronDown,
  Download,
  Palette,
  LogOut,
  History,
  MoreVertical,
  RotateCcw,
  Eraser
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { Alert } from '@components/ui/Alert';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { Pagination } from '@components/ui/Pagination';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import {
  ActionMenu,
  ActionMenuItem,
  ActionMenuDivider,
  ActionMenuDangerItem
} from '@components/ui/ActionMenu';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/AuthContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext';
import { useSessionPreferences } from '@contexts/SessionPreferencesContext';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useActivityTracker } from '@hooks/useActivityTracker';
import {
  type Session,
  type UserPreferences,
  type ThemeOption,
  refreshRateOptions,
  cleanIpAddress,
  showToast,
  parseUserAgent
} from './types';

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
  activeFilter?: 'all' | 'admin' | 'guest';
  onFilterChange?: (filter: 'all' | 'admin' | 'guest') => void;
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

const getRelativeTime = (dateString: string | null): string => {
  if (!dateString) return 'Never';
  const now = new Date();
  const rawStr =
    dateString.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(dateString) ? dateString : dateString + 'Z';
  const date = new Date(rawStr);
  const diffMs = now.getTime() - date.getTime();
  const diffSecs = Math.floor(diffMs / 1000);
  if (diffSecs < 60) return 'Just now';
  const diffMins = Math.floor(diffSecs / 60);
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.floor(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  return `${diffDays}d ago`;
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
  const [localFilter, setLocalFilter] = useState<'all' | 'admin' | 'guest'>('all');
  const activeFilterValue = controlledFilter ?? localFilter;
  const setActiveFilter = (filter: 'all' | 'admin' | 'guest') => {
    if (onFilterChange) {
      onFilterChange(filter);
    } else {
      setLocalFilter(filter);
    }
  };

  // Responsive state
  const [isMobile, setIsMobile] = useState(window.innerWidth < 1024);

  // Bulk actions state
  const [bulkMenuOpen, setBulkMenuOpen] = useState(false);
  const [showBulkResetConfirm, setShowBulkResetConfirm] = useState(false);
  const [showClearGuestsConfirm, setShowClearGuestsConfirm] = useState(false);
  const [bulkActionInProgress, setBulkActionInProgress] = useState<string | null>(null);

  // Session actions state
  const [revokingSession, setRevokingSession] = useState<string | null>(null);
  const [expandedSessions, setExpandedSessions] = useState<Set<string>>(new Set());
  const { isActive: isLocallyActive } = useActivityTracker();
  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [loggingOut, setLoggingOut] = useState(false);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);

  // Edit modal state
  const [editingSession, setEditingSession] = useState<Session | null>(null);
  const [editingPreferences, setEditingPreferences] = useState<UserPreferences | null>(null);
  const [pendingSteamPrefillChange, setPendingSteamPrefillChange] = useState<boolean | null>(null);
  const [pendingEpicPrefillChange, setPendingEpicPrefillChange] = useState<boolean | null>(null);
  const [loadingPreferences, setLoadingPreferences] = useState(false);
  const [savingPreferences, setSavingPreferences] = useState(false);

  // Pagination state
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalPages, setTotalPages] = useState(1);
  const [totalCount, setTotalCount] = useState(0);

  // History state
  const [historyExpanded, setHistoryExpanded] = useState(false);

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
    async (showLoading = false, page = currentPage) => {
      try {
        if (showLoading) {
          setLoading(true);
        }
        const response = await fetch(
          `/api/sessions?page=${page}&pageSize=${pageSize}`,
          ApiService.getFetchOptions()
        );

        if (response.ok) {
          const data = await response.json();
          const loadedSessions = data.sessions || [];
          setSessions(loadedSessions);
          setTotalPages(data.pagination?.totalPages || 1);
          setTotalCount(data.pagination?.totalCount || loadedSessions.length);
          setCurrentPage(data.pagination?.page || 1);
        } else {
          const errorData = await response.json();
          showToast('error', errorData.error || t('activeSessions.errors.loadSessions'));
        }
      } catch (err: unknown) {
        showToast('error', getErrorMessage(err) || t('activeSessions.errors.loadSessions'));
      } finally {
        setLoading(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [currentPage, pageSize, setLoading, setSessions]
  );

  const confirmRevokeSession = async () => {
    if (!pendingRevokeSession) return;

    const isOwnSession = pendingRevokeSession.isCurrentSession;

    try {
      setRevokingSession(pendingRevokeSession.id);
      const endpoint = `/api/sessions/${encodeURIComponent(pendingRevokeSession.id)}/revoke`;

      const response = await fetch(
        endpoint,
        ApiService.getFetchOptions({
          method: 'PATCH'
        })
      );

      if (response.ok) {
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
      } else {
        const errorData = await response.json();
        showToast(
          'error',
          errorData.message || errorData.error || t('activeSessions.errors.revokeSession')
        );
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.revokeSession'));
    } finally {
      setRevokingSession(null);
    }
  };

  const confirmDeleteSession = async () => {
    if (!pendingDeleteSession) return;

    const isOwnSession = pendingDeleteSession.isCurrentSession;

    try {
      setDeletingSession(pendingDeleteSession.id);
      const endpoint = `/api/sessions/${encodeURIComponent(pendingDeleteSession.id)}`;

      const response = await fetch(
        endpoint,
        ApiService.getFetchOptions({
          method: 'DELETE'
        })
      );

      if (response.ok) {
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
      } else {
        const errorData = await response.json();
        showToast(
          'error',
          errorData.message || errorData.error || t('activeSessions.errors.deleteSession')
        );
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.deleteSession'));
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
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.logout'));
    } finally {
      setLoggingOut(false);
    }
  };

  const handleEditSession = async (session: Session) => {
    setEditingSession(session);
    setPendingSteamPrefillChange(null);
    setPendingEpicPrefillChange(null);
    setLoadingPreferences(true);
    try {
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(session.id)}`,
        ApiService.getFetchOptions()
      );

      if (response.ok) {
        const prefs = await response.json();
        const selectedTheme =
          prefs.selectedTheme && prefs.selectedTheme.trim() !== '' ? prefs.selectedTheme : null;
        setEditingPreferences({
          selectedTheme: selectedTheme,
          sharpCorners: prefs.sharpCorners ?? false,
          disableFocusOutlines: prefs.disableFocusOutlines ?? true,
          disableTooltips: prefs.disableTooltips ?? false,
          picsAlwaysVisible: prefs.picsAlwaysVisible ?? false,
          disableStickyNotifications: prefs.disableStickyNotifications ?? false,
          showDatasourceLabels: prefs.showDatasourceLabels ?? true,
          useLocalTimezone: prefs.useLocalTimezone ?? false,
          use24HourFormat: prefs.use24HourFormat ?? true,
          showYearInDates: prefs.showYearInDates ?? false,
          refreshRate: prefs.refreshRate ?? null,
          refreshRateLocked: prefs.refreshRateLocked ?? null,
          allowedTimeFormats: prefs.allowedTimeFormats ?? undefined,
          maxThreadCount: prefs.maxThreadCount ?? null
        });
      } else {
        setEditingPreferences({
          selectedTheme: null,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false,
          picsAlwaysVisible: false,
          disableStickyNotifications: false,
          showDatasourceLabels: true,
          useLocalTimezone: false,
          use24HourFormat: true,
          showYearInDates: false,
          refreshRate: null,
          refreshRateLocked: null,
          allowedTimeFormats: undefined,
          maxThreadCount: null
        });
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.loadPreferences'));
      setEditingSession(null);
    } finally {
      setLoadingPreferences(false);
    }
  };

  const handleSavePreferences = async () => {
    if (!editingSession || !editingPreferences) return;

    try {
      setSavingPreferences(true);
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(editingSession.id)}`,
        ApiService.getFetchOptions({
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify(editingPreferences)
        })
      );

      if (response.ok) {
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
          await fetch(
            `/api/sessions/${encodeURIComponent(editingSession.id)}/refresh-rate`,
            ApiService.getFetchOptions({
              method: 'PATCH',
              headers: {
                'Content-Type': 'application/json'
              },
              body: JSON.stringify({ refreshRate: editingPreferences.refreshRate || '' })
            })
          );

          const prefillToggles: { service: string; enabled: boolean }[] = [];
          if (pendingSteamPrefillChange !== null) {
            prefillToggles.push({ service: 'steam', enabled: pendingSteamPrefillChange });
          }
          if (pendingEpicPrefillChange !== null) {
            prefillToggles.push({ service: 'epic', enabled: pendingEpicPrefillChange });
          }
          await Promise.all(
            prefillToggles.map(({ service, enabled }: { service: string; enabled: boolean }) =>
              fetch(
                `/api/auth/guest/prefill/toggle/${encodeURIComponent(editingSession.id)}?service=${service}`,
                ApiService.getFetchOptions({
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ enabled })
                })
              )
            )
          );
        }

        setEditingSession(null);
        setEditingPreferences(null);
        setPendingSteamPrefillChange(null);
        setPendingEpicPrefillChange(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('activeSessions.errors.savePreferences'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.savePreferences'));
    } finally {
      setSavingPreferences(false);
    }
  };

  const handleBulkResetToDefaults = async () => {
    try {
      setBulkActionInProgress('reset');
      const response = await fetch(
        '/api/sessions/bulk/reset-to-defaults',
        ApiService.getFetchOptions({
          method: 'POST'
        })
      );

      if (response.ok) {
        const data = await response.json();
        showToast('success', t('user.bulkActions.resetSuccess', { count: data.affectedCount }));
        setShowBulkResetConfirm(false);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.bulkActions.errors.resetFailed'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.bulkActions.errors.resetFailed'));
    } finally {
      setBulkActionInProgress(null);
    }
  };

  const handleClearAllGuests = async () => {
    try {
      setBulkActionInProgress('clear');
      const response = await fetch(
        '/api/sessions/bulk/clear-guests',
        ApiService.getFetchOptions({
          method: 'DELETE'
        })
      );

      if (response.ok) {
        const data = await response.json();
        showToast('success', t('user.bulkActions.clearSuccess', { count: data.clearedCount }));
        onSessionsChange();
        setShowClearGuestsConfirm(false);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.bulkActions.errors.clearFailed'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.bulkActions.errors.clearFailed'));
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
    (data: { maxThreadCount?: number | null }) => {
      loadSessions(false);
      if ('maxThreadCount' in data) {
        setEpicDefaultGuestMaxThreadCount(data.maxThreadCount ?? null);
      }
    },
    [loadSessions]
  );

  const handleGuestRefreshRateUpdated = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  // ============================================================
  // Helper Functions
  // ============================================================

  const toggleSessionExpanded = (sessionId: string) => {
    setExpandedSessions((prev) => {
      const next = new Set(prev);
      if (next.has(sessionId)) {
        next.delete(sessionId);
      } else {
        next.add(sessionId);
      }
      return next;
    });
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
      return `${hours}h ${minutes}m remaining`;
    }
    return `${minutes}m remaining`;
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

  const getCountForFilter = (filter: 'all' | 'admin' | 'guest'): number => {
    if (filter === 'all') return activeSessions.length;
    if (filter === 'admin') return activeSessions.filter((s: Session) => isAdminSession(s)).length;
    return activeSessions.filter((s: Session) => isGuestSession(s)).length;
  };

  const getFilterLabel = (filter: 'all' | 'admin' | 'guest'): string => {
    if (filter === 'all') return t('activeSessions.filters.all', 'All');
    if (filter === 'admin') return t('activeSessions.filters.admin', 'Admin');
    return t('activeSessions.filters.guest', 'Guest');
  };

  // ============================================================
  // useEffect Hooks
  // ============================================================

  // Responsive resize listener
  useEffect(() => {
    const handleResize = () => {
      setIsMobile(window.innerWidth < 1024);
    };
    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
  }, []);

  // Load default guest max thread count for both Steam and Epic
  useEffect(() => {
    const loadThreadConfig = async () => {
      try {
        const [steamRes, epicRes] = await Promise.all([
          fetch('/api/auth/guest/prefill/config', ApiService.getFetchOptions()),
          fetch('/api/auth/guest/prefill/config/epic', ApiService.getFetchOptions())
        ]);
        if (steamRes.ok) {
          const data = await steamRes.json();
          setDefaultGuestMaxThreadCount(data.maxThreadCount ?? null);
        }
        if (epicRes.ok) {
          const data = await epicRes.json();
          setEpicDefaultGuestMaxThreadCount(data.maxThreadCount ?? null);
        }
      } catch (err) {
        showToast('error', getErrorMessage(err) || t('user.errors.loadThreadConfig'));
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

  const activeSessions = sessions.filter((s: Session) => !s.isRevoked && !s.isExpired);
  const historySessions = sessions.filter((s: Session) => s.isRevoked || s.isExpired);

  const filteredActiveSessions =
    activeFilterValue === 'all'
      ? activeSessions
      : activeFilterValue === 'admin'
        ? activeSessions.filter((s: Session) => isAdminSession(s))
        : activeSessions.filter((s: Session) => isGuestSession(s));

  // ============================================================
  // Render Helpers: Desktop Table
  // ============================================================

  const renderTableRow = (session: Session) => {
    const sessionStatus = getSessionStatus(session);
    const parsedUA = parseUserAgent(session.userAgent);
    const isExpanded = expandedSessions.has(session.id);

    return (
      <React.Fragment key={session.id}>
        <tr
          className={`session-table-row session-table-row--${sessionStatus} cursor-pointer`}
          onClick={() => toggleSessionExpanded(session.id)}
        >
          {/* Status */}
          <td>
            <div className={`status-dot ${sessionStatus}`} />
          </td>

          {/* Device & Type */}
          <td>
            <div className="text-sm font-medium text-themed-primary truncate">{parsedUA.title}</div>
            <div className="flex items-center gap-1.5 mt-0.5">
              <span
                className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium ${isAdminSession(session) ? 'session-badge-user' : 'session-badge-guest'}`}
              >
                {isAdminSession(session)
                  ? t('activeSessions.labels.userBadge')
                  : t('activeSessions.labels.guestBadge')}
              </span>
              {session.isCurrentSession && (
                <span className="text-[10px] font-medium text-themed-success">
                  ({t('activeSessions.currentSessionShort', 'you')})
                </span>
              )}
              {!session.isCurrentSession &&
                isGuestSession(session) &&
                !session.isRevoked &&
                !session.isExpired && (
                  <span className="text-[10px] text-themed-muted">
                    {formatTimeRemaining(session.expiresAt)}
                  </span>
                )}
            </div>
          </td>

          {/* Network */}
          <td>
            {session.ipAddress && (
              <ClientIpDisplay
                clientIp={cleanIpAddress(session.ipAddress)}
                className="text-sm text-themed-secondary"
              />
            )}
          </td>

          {/* Last Seen */}
          <td>
            <span
              className="text-sm text-themed-secondary"
              title={
                session.lastSeenAt
                  ? new Date(
                      session.lastSeenAt.endsWith('Z') ||
                        /[+-]\d{2}:\d{2}$/.test(session.lastSeenAt)
                        ? session.lastSeenAt
                        : session.lastSeenAt + 'Z'
                    ).toLocaleString()
                  : ''
              }
            >
              {getRelativeTime(session.lastSeenAt)}
            </span>
          </td>

          {/* Actions */}
          <td>
            <div
              className="session-row-actions"
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
              {isGuestSession(session) &&
                !session.isRevoked &&
                !session.isExpired &&
                !session.isCurrentSession && (
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
                  variant="default"
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
          </td>
        </tr>

        {/* Expansion panel */}
        <tr>
          <td colSpan={5} className="p-0">
            <div
              className={`session-expansion ${isExpanded ? 'session-expansion--expanded' : 'session-expansion--collapsed'}`}
            >
              {isExpanded && (
                <div className="session-expansion-content">
                  <div className="session-expansion-dates">
                    <div>
                      <div className="session-expansion-date-label">
                        {t('activeSessions.labels.createdShort', 'Created')}
                      </div>
                      <div className="session-expansion-date-value">
                        <FormattedTimestamp timestamp={session.createdAt} />
                      </div>
                    </div>
                    <div>
                      <div className="session-expansion-date-label">
                        {t('activeSessions.labels.lastSeenShort', 'Last Seen')}
                      </div>
                      <div className="session-expansion-date-value">
                        {session.lastSeenAt ? (
                          <FormattedTimestamp timestamp={session.lastSeenAt} />
                        ) : (
                          t('activeSessions.labels.never', 'Never')
                        )}
                      </div>
                    </div>
                    <div>
                      <div className="session-expansion-date-label">
                        {t('activeSessions.labels.expires', 'Expires')}
                      </div>
                      <div className="session-expansion-date-value">
                        <FormattedTimestamp timestamp={session.expiresAt} />
                      </div>
                    </div>
                    {session.revokedAt && (
                      <div>
                        <div className="session-expansion-date-label session-expansion-date-label--error">
                          {t('activeSessions.labels.revokedShort', 'Revoked')}
                        </div>
                        <div className="session-expansion-date-value session-expansion-date-value--error">
                          <FormattedTimestamp timestamp={session.revokedAt} />
                        </div>
                      </div>
                    )}
                  </div>

                  {/* Session ID */}
                  <div className="session-expansion-session-id">
                    {t('activeSessions.labels.sessionIdWithValue', { id: session.id })}
                  </div>

                  {/* Preferences summary badges */}
                  <div className="session-expansion-badges">
                    {renderExpansionPreferences(session)}
                  </div>

                  {/* Prefill permissions per service */}
                  {isGuestSession(session) && !session.isRevoked && !session.isExpired && (
                    <div className="session-expansion-prefill">
                      <span className="session-expansion-prefill-label">
                        {t('activeSessions.prefill.title', 'Prefill Access')}:
                      </span>
                      <span
                        className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium inline-flex items-center gap-1 ${session.steamPrefillEnabled ? 'status-badge-success' : 'status-badge-warning'}`}
                      >
                        <SteamIcon size={10} />
                        Steam
                      </span>
                      <span
                        className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium inline-flex items-center gap-1 ${session.epicPrefillEnabled ? 'status-badge-success' : 'status-badge-warning'}`}
                      >
                        <EpicIcon size={10} />
                        Epic
                      </span>
                    </div>
                  )}
                </div>
              )}
            </div>
          </td>
        </tr>
      </React.Fragment>
    );
  };

  const renderExpansionPreferences = (session: Session) => {
    const prefs = getSessionPreferences(session.id);
    const isLoadingPrefs = isPreferencesLoading(session.id);

    if (
      !prefs &&
      !isLoadingPrefs &&
      !isPreferencesLoaded(session.id) &&
      !session.isRevoked &&
      !session.isExpired
    ) {
      setTimeout(() => loadSessionPreferences(session.id), 0);
    }

    if (isLoadingPrefs) {
      return (
        <div className="flex items-center gap-2 text-xs text-themed-muted">
          <Loader2 className="w-3 h-3 animate-spin" />
          {t('activeSessions.preferencesModal.loading', 'Loading preferences...')}
        </div>
      );
    }

    if (!prefs) return null;

    const themeName = prefs.selectedTheme
      ? availableThemes.find((th: ThemeOption) => th.id === prefs.selectedTheme)?.name ||
        prefs.selectedTheme
      : t('activeSessions.preferencesModal.defaultThemeShort', 'Default');
    const timezoneLabel = prefs.useLocalTimezone
      ? t('activeSessions.labels.local', 'Local')
      : t('activeSessions.labels.server', 'Server');

    return (
      <>
        <span className="pref-badge">
          <Palette className="w-3 h-3" />
          {themeName}
        </span>
        <span className="pref-badge">
          <Globe className="w-3 h-3" />
          {timezoneLabel}
        </span>
        {prefs.sharpCorners && <span className="pref-badge">Sharp corners</span>}
        {prefs.showDatasourceLabels && <span className="pref-badge">Labels</span>}
      </>
    );
  };

  // ============================================================
  // Render Helpers: Mobile Card
  // ============================================================

  const renderMobileCard = (session: Session) => {
    const isExpanded = expandedSessions.has(session.id);
    const sessionStatus = getSessionStatus(session);
    const isActive = sessionStatus === 'active';
    const isAway = sessionStatus === 'away';
    const isDimmed = session.isExpired || session.isRevoked;
    const parsedUA = parseUserAgent(session.userAgent);

    const prefs = getSessionPreferences(session.id);
    const isLoadingPrefs = isPreferencesLoading(session.id);

    if (
      !prefs &&
      !isLoadingPrefs &&
      !isPreferencesLoaded(session.id) &&
      !session.isRevoked &&
      !session.isExpired
    ) {
      setTimeout(() => loadSessionPreferences(session.id), 0);
    }

    const themeName = prefs?.selectedTheme
      ? availableThemes.find((th: ThemeOption) => th.id === prefs.selectedTheme)?.name ||
        prefs.selectedTheme
      : t('activeSessions.preferencesModal.defaultThemeShort');
    const timezoneLabel = prefs?.useLocalTimezone
      ? t('activeSessions.labels.local')
      : t('activeSessions.labels.server');

    return (
      <div key={session.id} className={`session-card ${isDimmed ? 'dimmed' : ''}`}>
        {/* Header - Always visible */}
        <div className="p-3">
          <div className="cursor-pointer" onClick={() => toggleSessionExpanded(session.id)}>
            <div className="flex items-start gap-3">
              <div className="relative">
                <div
                  className={`session-avatar ${
                    isAdminSession(session) ? 'session-badge-user' : 'session-badge-guest'
                  }`}
                >
                  <User
                    className={`w-5 h-5 ${
                      isAdminSession(session) ? 'user-session-icon' : 'guest-session-icon'
                    }`}
                  />
                </div>
                {(isActive || isAway) && (
                  <div
                    className={`status-dot ${isActive ? 'active' : 'away'} absolute -bottom-0.5 -right-0.5`}
                  />
                )}
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap mb-1">
                  <h3 className="font-semibold truncate text-sm text-themed-primary">
                    {parsedUA.title}
                  </h3>
                  <span
                    className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium flex-shrink-0 ${
                      isAdminSession(session) ? 'session-badge-user' : 'session-badge-guest'
                    }`}
                  >
                    {isAdminSession(session)
                      ? t('activeSessions.labels.userBadge')
                      : t('activeSessions.labels.guestBadge')}
                  </span>
                </div>
                <p className="text-xs truncate text-themed-muted">
                  {session.isCurrentSession
                    ? t('activeSessions.currentSession')
                    : session.ipAddress
                      ? cleanIpAddress(session.ipAddress)
                      : t('activeSessions.unknownDeviceLower')}
                </p>
                <div className="flex items-center gap-1.5 mt-1.5 flex-wrap">
                  {isGuestSession(session) && !session.isRevoked && !session.isExpired && (
                    <span className="px-1.5 py-0.5 text-[10px] rounded font-medium status-badge-warning">
                      {formatTimeRemaining(session.expiresAt)}
                    </span>
                  )}
                  {session.isRevoked && (
                    <span className="px-1.5 py-0.5 text-[10px] rounded font-medium status-badge-error">
                      {t('activeSessions.status.revoked')}
                    </span>
                  )}
                  {session.isExpired && !session.isRevoked && (
                    <span className="px-1.5 py-0.5 text-[10px] rounded font-medium status-badge-warning">
                      {t('activeSessions.prefill.status.expired')}
                    </span>
                  )}
                  {!session.isRevoked && !session.isExpired && prefs && (
                    <>
                      <span className="pref-badge text-[10px]">
                        <Palette className="w-3 h-3" />
                        {themeName}
                      </span>
                      <span className="pref-badge text-[10px]">
                        <Globe className="w-3 h-3" />
                        {timezoneLabel}
                      </span>
                    </>
                  )}
                </div>
              </div>
              <ChevronDown
                className={`w-5 h-5 flex-shrink-0 transition-transform duration-200 text-themed-muted ${isExpanded ? 'rotate-180' : ''}`}
              />
            </div>
          </div>
        </div>

        {/* Mobile expanded content */}
        <div
          className={`overflow-hidden transition-all duration-200 ${isExpanded ? 'max-h-[500px] opacity-100' : 'max-h-0 opacity-0'}`}
        >
          <div
            className={`px-3 pb-3 space-y-3 border-t border-themed-secondary ${isDimmed ? 'opacity-60' : ''}`}
          >
            <div className="space-y-2.5 pt-3">
              {session.ipAddress && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                    <Network className="w-3 h-3" />
                    <span>{t('activeSessions.labels.ipAddress')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-primary">
                    <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} />
                  </div>
                </div>
              )}
              {session.isCurrentSession && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-success">
                    <User className="w-3 h-3" />
                    <span>{t('activeSessions.currentSession')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-success">
                    {t('activeSessions.thisDevice')}
                  </div>
                </div>
              )}
              <div>
                <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                  <Clock className="w-3 h-3" />
                  <span>{t('activeSessions.labels.createdShort')}</span>
                </div>
                <div className="text-sm font-medium pl-[18px] text-themed-primary">
                  <FormattedTimestamp timestamp={session.createdAt} />
                </div>
              </div>
              {session.lastSeenAt && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                    <Clock className="w-3 h-3" />
                    <span>{t('activeSessions.labels.lastSeenShort')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-primary">
                    <FormattedTimestamp timestamp={session.lastSeenAt} />
                  </div>
                </div>
              )}
              {session.revokedAt && isGuestSession(session) && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-error">
                    <Clock className="w-3 h-3" />
                    <span>{t('activeSessions.labels.revokedShort')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-error">
                    <FormattedTimestamp timestamp={session.revokedAt} />
                  </div>
                </div>
              )}

              {/* Prefill permissions */}
              {isGuestSession(session) && !session.isRevoked && !session.isExpired && (
                <div className="flex items-center gap-2 pt-1">
                  <span className="text-xs text-themed-muted">
                    {t('activeSessions.prefill.title', 'Prefill')}:
                  </span>
                  <span
                    className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium inline-flex items-center gap-1 ${session.steamPrefillEnabled ? 'status-badge-success' : 'status-badge-warning'}`}
                  >
                    <SteamIcon size={10} />
                    Steam
                  </span>
                  <span
                    className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium inline-flex items-center gap-1 ${session.epicPrefillEnabled ? 'status-badge-success' : 'status-badge-warning'}`}
                  >
                    <EpicIcon size={10} />
                    Epic
                  </span>
                </div>
              )}

              <div>
                <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                  <span>{t('activeSessions.labels.sessionId')}</span>
                </div>
                <div className="text-xs font-mono break-all text-themed-secondary">
                  {session.id}
                </div>
              </div>
            </div>

            {/* Mobile action buttons */}
            <div className="flex flex-col gap-2 pt-2">
              {session.isCurrentSession && (
                <Button
                  variant="default"
                  color="orange"
                  size="sm"
                  leftSection={<LogOut className="w-4 h-4" />}
                  onClick={(e: React.MouseEvent) => {
                    e.stopPropagation();
                    handleLogout();
                  }}
                  disabled={loggingOut}
                  loading={loggingOut}
                  fullWidth
                >
                  {t('activeSessions.actions.logout')}
                </Button>
              )}
              <Button
                variant="default"
                color="blue"
                size="sm"
                leftSection={<Edit className="w-4 h-4" />}
                onClick={(e: React.MouseEvent) => {
                  e.stopPropagation();
                  handleEditSession(session);
                }}
                fullWidth
              >
                {t('activeSessions.actions.editPreferences')}
              </Button>
              {isGuestSession(session) &&
                !session.isRevoked &&
                !session.isExpired &&
                !session.isCurrentSession && (
                  <Button
                    variant="default"
                    color="orange"
                    size="sm"
                    onClick={(e: React.MouseEvent) => {
                      e.stopPropagation();
                      handleRevokeSession(session);
                    }}
                    disabled={revokingSession === session.id}
                    fullWidth
                  >
                    {revokingSession === session.id
                      ? t('activeSessions.actions.revoking')
                      : t('activeSessions.actions.revokeSession')}
                  </Button>
                )}
              {!session.isCurrentSession && (
                <Button
                  variant="default"
                  color="red"
                  size="sm"
                  leftSection={<Trash2 className="w-4 h-4" />}
                  onClick={(e: React.MouseEvent) => {
                    e.stopPropagation();
                    handleDeleteSession(session);
                  }}
                  disabled={deletingSession === session.id}
                  fullWidth
                >
                  {deletingSession === session.id
                    ? t('activeSessions.actions.deleting')
                    : t('activeSessions.actions.deleteSession')}
                </Button>
              )}
            </div>
          </div>
        </div>
      </div>
    );
  };

  // ============================================================
  // Render Helpers: History Card
  // ============================================================

  const renderHistoryCard = (session: Session) => {
    const parsedUA = parseUserAgent(session.userAgent);

    return (
      <div key={session.id} className="session-card dimmed">
        <div className="p-3 sm:p-4">
          <div className="flex items-start justify-between gap-4">
            <div className="flex items-start gap-3 flex-1 min-w-0">
              <div
                className={`session-avatar ${isAdminSession(session) ? 'session-badge-user' : 'session-badge-guest'}`}
              >
                <User
                  className={`w-5 h-5 ${isAdminSession(session) ? 'user-session-icon' : 'guest-session-icon'}`}
                />
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap mb-1">
                  <h3 className="font-semibold truncate text-sm text-themed-primary">
                    {parsedUA.title}
                  </h3>
                  <span
                    className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium flex-shrink-0 ${isAdminSession(session) ? 'session-badge-user' : 'session-badge-guest'}`}
                  >
                    {isAdminSession(session)
                      ? t('activeSessions.labels.userBadge')
                      : t('activeSessions.labels.guestBadge')}
                  </span>
                  {session.isRevoked && (
                    <span className="px-1.5 py-0.5 text-[10px] rounded font-medium status-badge-error">
                      {t('activeSessions.status.revoked')}
                    </span>
                  )}
                  {session.isExpired && !session.isRevoked && (
                    <span className="px-1.5 py-0.5 text-[10px] rounded font-medium status-badge-warning">
                      {t('activeSessions.prefill.status.expired')}
                    </span>
                  )}
                </div>
                <div className="flex items-center gap-4 text-xs text-themed-muted">
                  {session.ipAddress && (
                    <span className="flex items-center gap-1">
                      <Network className="w-3 h-3" />
                      <ClientIpDisplay
                        clientIp={cleanIpAddress(session.ipAddress)}
                        className="truncate"
                      />
                    </span>
                  )}
                  <span className="flex items-center gap-1">
                    <Clock className="w-3 h-3" />
                    <FormattedTimestamp timestamp={session.createdAt} />
                  </span>
                  {session.revokedAt && (
                    <span className="flex items-center gap-1 text-themed-error">
                      <Clock className="w-3 h-3" />
                      {t('activeSessions.labels.revokedAt')}{' '}
                      <FormattedTimestamp timestamp={session.revokedAt} />
                    </span>
                  )}
                </div>
                <div className="text-[10px] font-mono truncate mt-1 text-themed-muted">
                  {session.id}
                </div>
              </div>
            </div>
            <div className="flex gap-2 items-start flex-shrink-0">
              <Button
                variant="default"
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
        </div>
      </div>
    );
  };

  // ============================================================
  // Render: Main
  // ============================================================

  return (
    <div className="active-sessions-layout">
      <Card padding="none">
        {/* ---- Header: Title + Guest Lock ---- */}
        <div className="p-4 sm:p-5 border-b border-themed-secondary">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-semibold text-themed-primary">
                {t('activeSessions.title')}
              </h2>
              <HelpPopover width={320}>
                <HelpSection title={t('activeSessions.help.sessionTypes.title')} variant="subtle">
                  <div className="divide-y divide-[var(--theme-text-muted)]">
                    <div className="py-1.5 first:pt-0 last:pb-0">
                      <div className="font-medium text-themed-primary">
                        {t('activeSessions.help.sessionTypes.authenticated.label')}
                      </div>
                      <div className="mt-0.5">
                        {t('activeSessions.help.sessionTypes.authenticated.description')}
                      </div>
                    </div>
                    <div className="py-1.5 first:pt-0 last:pb-0">
                      <div className="font-medium text-themed-primary">
                        {t('activeSessions.help.sessionTypes.guest.label')}
                      </div>
                      <div className="mt-0.5">
                        {t('activeSessions.help.sessionTypes.guest.description', {
                          hours: guestDurationHours
                        })}
                      </div>
                    </div>
                  </div>
                </HelpSection>
                <HelpSection title={t('activeSessions.help.actions.title')} variant="subtle">
                  <div className="divide-y divide-[var(--theme-text-muted)]">
                    <div className="py-1.5 first:pt-0 last:pb-0">
                      <div className="font-medium text-themed-primary">
                        {t('activeSessions.help.actions.revoke.label')}
                      </div>
                      <div className="mt-0.5">
                        {t('activeSessions.help.actions.revoke.description')}
                      </div>
                    </div>
                    <div className="py-1.5 first:pt-0 last:pb-0">
                      <div className="font-medium text-themed-primary">
                        {t('activeSessions.help.actions.delete.label')}
                      </div>
                      <div className="mt-0.5">
                        {t('activeSessions.help.actions.delete.description')}
                      </div>
                    </div>
                  </div>
                </HelpSection>
              </HelpPopover>
            </div>

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

        {/* ---- Sub-header: Filter Chips + Bulk Actions ---- */}
        {!loading && activeSessions.length > 0 && (
          <div className="px-4 sm:px-5 py-3 border-b border-themed-secondary">
            <div className="flex items-center justify-between flex-wrap gap-2">
              {/* Filter Chips */}
              <div className="flex flex-wrap items-center gap-2">
                {(['all', 'admin', 'guest'] as const).map((filter: 'all' | 'admin' | 'guest') => (
                  <button
                    key={filter}
                    className={`filter-chip ${activeFilterValue === filter ? 'filter-chip--active' : ''}`}
                    onClick={() => setActiveFilter(filter)}
                  >
                    <span className="filter-chip-count">{getCountForFilter(filter)}</span>
                    <span>{getFilterLabel(filter)}</span>
                  </button>
                ))}
              </div>

              {/* Bulk Actions Dropdown */}
              <ActionMenu
                isOpen={bulkMenuOpen}
                onClose={() => setBulkMenuOpen(false)}
                trigger={
                  <Button
                    variant="outline"
                    size="sm"
                    leftSection={<MoreVertical className="w-4 h-4" />}
                    onClick={() => setBulkMenuOpen((prev: boolean) => !prev)}
                  >
                    {t('user.bulkActions.title', 'Actions')}
                  </Button>
                }
                width="w-56"
              >
                <ActionMenuItem
                  icon={<RotateCcw className="w-4 h-4" />}
                  onClick={() => {
                    setBulkMenuOpen(false);
                    setShowBulkResetConfirm(true);
                  }}
                >
                  {t('user.bulkActions.buttons.reset', 'Reset All to Defaults')}
                </ActionMenuItem>
                <ActionMenuDivider />
                <ActionMenuDangerItem
                  icon={<Eraser className="w-4 h-4" />}
                  onClick={() => {
                    setBulkMenuOpen(false);
                    setShowClearGuestsConfirm(true);
                  }}
                >
                  {t('user.bulkActions.buttons.clear', 'Clear All Guest Sessions')}
                </ActionMenuDangerItem>
              </ActionMenu>
            </div>
          </div>
        )}

        {/* ---- Main Content ---- */}
        <div className="p-4 sm:p-5">
          {loading && (
            <div className="text-center py-12">
              <Loader2 className="w-8 h-8 animate-spin mx-auto text-themed-accent" />
              <p className="text-sm mt-3 text-themed-muted">{t('activeSessions.loading')}</p>
            </div>
          )}

          {!loading && activeSessions.length === 0 && (
            <div className="text-center py-12">
              <div className="w-16 h-16 mx-auto mb-4 rounded-xl flex items-center justify-center bg-themed-tertiary">
                <Users className="w-8 h-8 text-themed-muted" />
              </div>
              <p className="font-medium text-themed-secondary">{t('activeSessions.empty.title')}</p>
              <p className="text-sm mt-1 text-themed-muted">{t('activeSessions.empty.subtitle')}</p>
            </div>
          )}

          {/* Desktop: Data Table */}
          {!loading && filteredActiveSessions.length > 0 && !isMobile && (
            <table className="session-table">
              <thead className="session-table-header">
                <tr>
                  <th className="w-8">{t('activeSessions.table.status', 'Status')}</th>
                  <th>{t('activeSessions.table.device', 'Device & Type')}</th>
                  <th>{t('activeSessions.table.network', 'Network')}</th>
                  <th>{t('activeSessions.table.lastSeen', 'Last Seen')}</th>
                  <th className="w-1">{t('activeSessions.table.actions', 'Actions')}</th>
                </tr>
              </thead>
              <tbody>{filteredActiveSessions.map(renderTableRow)}</tbody>
            </table>
          )}

          {/* Mobile: Cards */}
          {!loading && filteredActiveSessions.length > 0 && isMobile && (
            <div className="space-y-2">{filteredActiveSessions.map(renderMobileCard)}</div>
          )}

          {/* Filtered but no results */}
          {!loading && activeSessions.length > 0 && filteredActiveSessions.length === 0 && (
            <div className="text-center py-8">
              <p className="text-sm text-themed-muted">
                {t('activeSessions.empty.filtered', 'No sessions match the selected filter.')}
              </p>
            </div>
          )}
        </div>

        {/* ---- Pagination ---- */}
        {!loading && totalPages > 1 && (
          <div className="px-4 sm:px-5 py-3 border-t border-themed-secondary">
            <Pagination
              currentPage={currentPage}
              totalPages={totalPages}
              totalItems={totalCount}
              itemsPerPage={pageSize}
              onPageChange={(newPage: number) => {
                setCurrentPage(newPage);
                loadSessions(true, newPage);
              }}
              itemLabel={t('activeSessions.paginationLabel')}
              showCard={false}
            />
          </div>
        )}
      </Card>

      {/* ============================================================ */}
      {/* Session History */}
      {/* ============================================================ */}

      {!loading && historySessions.length > 0 && (
        <Card padding="none">
          <div
            className="p-4 sm:p-5 cursor-pointer select-none"
            onClick={() => setHistoryExpanded((prev: boolean) => !prev)}
          >
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <History className="w-5 h-5 text-themed-muted" />
                <h2 className="text-lg font-semibold text-themed-primary">
                  {t('activeSessions.history.title')}
                </h2>
                <span className="px-2 py-0.5 text-xs rounded-full font-medium bg-themed-tertiary text-themed-muted">
                  {historySessions.length}
                </span>
              </div>
              <ChevronDown
                className={`w-5 h-5 transition-transform duration-200 text-themed-muted ${historyExpanded ? 'rotate-180' : ''}`}
              />
            </div>
          </div>
          {historyExpanded && (
            <div className="px-4 sm:px-5 pb-4 sm:pb-5 space-y-2">
              {historySessions.map(renderHistoryCard)}
            </div>
          )}
        </Card>
      )}

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
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
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
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
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
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
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
              <Loader2 className="w-8 h-8 animate-spin mx-auto text-themed-muted" />
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
                        <p className="text-sm text-themed-primary">Allow guest to change rate</p>
                        <p className="text-xs text-themed-muted">
                          {editingPreferences.refreshRateLocked === null
                            ? 'Using global default'
                            : editingPreferences.refreshRateLocked
                              ? 'Locked for this guest'
                              : 'Unlocked for this guest'}
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
                          Use Default
                        </button>
                      )}
                      <div
                        className={`modern-toggle cursor-pointer ${editingPreferences.refreshRateLocked === false ? 'checked' : ''}`}
                        onClick={() =>
                          setEditingPreferences({
                            ...editingPreferences,
                            refreshRateLocked: editingPreferences.refreshRateLocked !== false
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
                  <div className="prefill-access-section">
                    <div className="prefill-access-header">
                      <Download className="w-4 h-4 text-themed-accent" />
                      <h4 className="text-sm font-medium text-themed-primary">
                        {t('activeSessions.prefill.title')}
                      </h4>
                    </div>
                    <p className="text-xs text-themed-muted prefill-access-subtitle">
                      {t('activeSessions.prefill.subtitle')}
                    </p>

                    {/* Steam Prefill Row */}
                    {(() => {
                      const current = editingSession.steamPrefillEnabled;
                      const effective =
                        pendingSteamPrefillChange !== null ? pendingSteamPrefillChange : current;
                      const hasChange =
                        pendingSteamPrefillChange !== null && pendingSteamPrefillChange !== current;
                      return (
                        <div className="prefill-service-row">
                          <div className="prefill-service-row-label">
                            <SteamIcon size={16} className="prefill-service-row-icon" />
                            <span className="text-sm text-themed-secondary">Steam</span>
                            {hasChange && (
                              <span className="text-xs text-themed-accent italic">
                                ({t('common.unsaved')})
                              </span>
                            )}
                          </div>
                          <div className="prefill-service-row-controls">
                            <span
                              className={`px-2 py-0.5 text-xs rounded-full font-medium ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
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
                        <div className="prefill-service-row">
                          <div className="prefill-service-row-label">
                            <EpicIcon size={16} className="prefill-service-row-icon" />
                            <span className="text-sm text-themed-secondary">Epic Games</span>
                            {hasChange && (
                              <span className="text-xs text-themed-accent italic">
                                ({t('common.unsaved')})
                              </span>
                            )}
                          </div>
                          <div className="prefill-service-row-controls">
                            <span
                              className={`px-2 py-0.5 text-xs rounded-full font-medium ${effective ? 'status-badge-success' : 'status-badge-warning'}`}
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
                      label: `${n} threads`
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
