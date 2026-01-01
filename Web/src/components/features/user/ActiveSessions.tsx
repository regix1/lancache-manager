import React, { useEffect, useState, useCallback } from 'react';
import {
  Users,
  User,
  Trash2,
  Loader2,
  AlertTriangle,
  Clock,
  Network,
  Monitor,
  Globe,
  MapPin,
  Edit,
  Lock,
  Unlock,
  ChevronDown
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { Pagination } from '@components/ui/Pagination';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/AuthContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext';
import { useDefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useActivityTracker } from '@hooks/useActivityTracker';
import {
  Session,
  UserPreferences,
  ThemeOption,
  refreshRateOptions,
  cleanIpAddress,
  showToast
} from './types';

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
}

const timeFormatOptions = [
  {
    value: 'server-24h',
    label: 'Server (24h)',
    description: 'Server timezone, 24-hour format',
    icon: Globe
  },
  {
    value: 'server-12h',
    label: 'Server (12h)',
    description: 'Server timezone, 12-hour format',
    icon: Globe
  },
  {
    value: 'local-24h',
    label: 'Local (24h)',
    description: 'Local timezone, 24-hour format',
    icon: MapPin
  },
  {
    value: 'local-12h',
    label: 'Local (12h)',
    description: 'Local timezone, 12-hour format',
    icon: MapPin
  }
];

// Helper to format timestamp with timezone awareness
const FormattedTimestamp: React.FC<{ timestamp: string }> = ({ timestamp }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return <>{formattedTime}</>;
};

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
  onSessionsChange
}) => {
  const { refreshAuth } = useAuth();
  const { on, off } = useSignalR();
  const { prefs: defaultGuestPrefs } = useDefaultGuestPreferences();
  const [revokingSession, setRevokingSession] = useState<string | null>(null);
  const [expandedSessions, setExpandedSessions] = useState<Set<string>>(new Set());
  const { isActive: isLocallyActive } = useActivityTracker();
  const currentDeviceId = authService.getDeviceId();

  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);
  const [editingSession, setEditingSession] = useState<Session | null>(null);
  const [editingPreferences, setEditingPreferences] = useState<UserPreferences | null>(null);
  const [loadingPreferences, setLoadingPreferences] = useState(false);
  const [savingPreferences, setSavingPreferences] = useState(false);
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalPages, setTotalPages] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [sessionPreferences, setSessionPreferences] = useState<Record<string, UserPreferences>>({});
  const [loadingSessionPrefs, setLoadingSessionPrefs] = useState<Set<string>>(new Set());

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

  // Load preferences for a specific session (for card preview)
  const loadSessionPreferences = useCallback(
    async (sessionId: string) => {
      if (sessionPreferences[sessionId] || loadingSessionPrefs.has(sessionId)) {
        return;
      }

      setLoadingSessionPrefs((prev) => new Set(prev).add(sessionId));

      try {
        const response = await fetch(
          `/api/user-preferences/session/${encodeURIComponent(sessionId)}`,
          { headers: ApiService.getHeaders() }
        );

        if (response.ok) {
          const prefs = await response.json();
          setSessionPreferences((prev) => ({
            ...prev,
            [sessionId]: {
              selectedTheme: prefs.selectedTheme || null,
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
              allowedTimeFormats: prefs.allowedTimeFormats ?? undefined
            }
          }));
        }
      } catch (err) {
        console.error('Failed to load session preferences:', err);
      } finally {
        setLoadingSessionPrefs((prev) => {
          const next = new Set(prev);
          next.delete(sessionId);
          return next;
        });
      }
    },
    [sessionPreferences, loadingSessionPrefs]
  );

  const loadSessions = useCallback(
    async (showLoading = false, page = currentPage) => {
      try {
        if (showLoading) {
          setLoading(true);
        }
        const response = await fetch(`/api/sessions?page=${page}&pageSize=${pageSize}`, {
          headers: ApiService.getHeaders()
        });

        if (response.ok) {
          const data = await response.json();
          const loadedSessions = data.sessions || [];
          setSessions(loadedSessions);
          setTotalPages(data.pagination?.totalPages || 1);
          setTotalCount(data.pagination?.totalCount || loadedSessions.length);
          setCurrentPage(data.pagination?.page || 1);
        } else {
          const errorData = await response.json();
          showToast('error', errorData.error || 'Failed to load sessions');
        }
      } catch (err: unknown) {
        showToast('error', getErrorMessage(err) || 'Failed to load sessions');
      } finally {
        if (showLoading) {
          setLoading(false);
        }
      }
    },
    [currentPage, pageSize, setLoading, setSessions]
  );

  const handleSessionRevoked = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionsCleared = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionCreated = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionLastSeenUpdated = useCallback(
    (data: { deviceId: string; lastSeenAt: string }) => {
      setSessions((prev) =>
        prev.map((session) => {
          if (session.id === data.deviceId || session.deviceId === data.deviceId) {
            return { ...session, lastSeenAt: data.lastSeenAt };
          }
          return session;
        })
      );
    },
    [setSessions]
  );

  const handleUserPreferencesUpdated = useCallback(
    (data: { sessionId: string; preferences: UserPreferences }) => {
      setSessionPreferences((prev) => ({
        ...prev,
        [data.sessionId]: data.preferences
      }));
    },
    []
  );

  useEffect(() => {
    loadSessions(true);

    on('UserSessionRevoked', handleSessionRevoked);
    on('UserSessionsCleared', handleSessionsCleared);
    on('UserSessionCreated', handleSessionCreated);
    on('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
    on('UserPreferencesUpdated', handleUserPreferencesUpdated);

    const pollInterval = setInterval(() => {
      loadSessions(false);
    }, 30000);

    const handleVisibilityChange = () => {
      if (!document.hidden) {
        loadSessions(false);
      }
    };
    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      off('UserSessionRevoked', handleSessionRevoked);
      off('UserSessionsCleared', handleSessionsCleared);
      off('UserSessionCreated', handleSessionCreated);
      off('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
      off('UserPreferencesUpdated', handleUserPreferencesUpdated);
      clearInterval(pollInterval);
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [
    loadSessions,
    on,
    off,
    handleSessionRevoked,
    handleSessionsCleared,
    handleSessionCreated,
    handleSessionLastSeenUpdated,
    handleUserPreferencesUpdated
  ]);

  const handleRevokeSession = (session: Session) => {
    setPendingRevokeSession(session);
  };

  const confirmRevokeSession = async () => {
    if (!pendingRevokeSession) return;

    const isOwnSession =
      (pendingRevokeSession.type === 'authenticated' &&
        pendingRevokeSession.id === authService.getDeviceId()) ||
      (pendingRevokeSession.type === 'guest' &&
        pendingRevokeSession.id === authService.getGuestSessionId());

    try {
      setRevokingSession(pendingRevokeSession.id);
      const endpoint = `/api/sessions/${encodeURIComponent(pendingRevokeSession.id)}?action=revoke`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        if (isOwnSession) {
          setPendingRevokeSession(null);
          showToast('info', 'You revoked your own session. Logging out...');

          setTimeout(async () => {
            authService.clearAuth();
            await refreshAuth();
          }, 2000);
          return;
        }

        await loadSessions(false);
        setPendingRevokeSession(null);
        onSessionsChange();
      } else {
        const errorData = await response.json();
        showToast('error', errorData.message || errorData.error || 'Failed to revoke session');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to revoke session');
    } finally {
      setRevokingSession(null);
    }
  };

  const handleDeleteSession = (session: Session) => {
    setPendingDeleteSession(session);
  };

  const confirmDeleteSession = async () => {
    if (!pendingDeleteSession) return;

    const isOwnSession =
      (pendingDeleteSession.type === 'authenticated' &&
        pendingDeleteSession.id === authService.getDeviceId()) ||
      (pendingDeleteSession.type === 'guest' &&
        pendingDeleteSession.id === authService.getGuestSessionId());

    try {
      setDeletingSession(pendingDeleteSession.id);
      const endpoint = `/api/sessions/${encodeURIComponent(pendingDeleteSession.id)}?action=delete`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        if (isOwnSession) {
          setPendingDeleteSession(null);
          showToast('info', 'You deleted your own session. Logging out...');

          setTimeout(async () => {
            authService.clearAuth();
            await refreshAuth();
          }, 2000);
          return;
        }

        await loadSessions(false);
        setPendingDeleteSession(null);
        onSessionsChange();
      } else {
        const errorData = await response.json();
        showToast('error', errorData.message || errorData.error || 'Failed to delete session');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to delete session');
    } finally {
      setDeletingSession(null);
    }
  };

  const handleEditSession = async (session: Session) => {
    setEditingSession(session);
    setLoadingPreferences(true);
    try {
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(session.id)}`,
        { headers: ApiService.getHeaders() }
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
          allowedTimeFormats: prefs.allowedTimeFormats ?? undefined
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
          allowedTimeFormats: undefined
        });
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to load user preferences');
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
        {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            ...ApiService.getHeaders()
          },
          body: JSON.stringify(editingPreferences)
        }
      );

      if (response.ok) {
        const isOwnSession =
          (editingSession.type === 'authenticated' &&
            editingSession.id === authService.getDeviceId()) ||
          (editingSession.type === 'guest' &&
            editingSession.id === authService.getGuestSessionId());

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

          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: {
                key: 'showDatasourceLabels',
                value: editingPreferences.showDatasourceLabels
              }
            })
          );
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'useLocalTimezone', value: editingPreferences.useLocalTimezone }
            })
          );
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'use24HourFormat', value: editingPreferences.use24HourFormat }
            })
          );
          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'showYearInDates', value: editingPreferences.showYearInDates }
            })
          );
          if (editingPreferences.allowedTimeFormats) {
            window.dispatchEvent(
              new CustomEvent('preference-changed', {
                detail: { key: 'allowedTimeFormats', value: editingPreferences.allowedTimeFormats }
              })
            );
          }
        }

        if (editingSession.type === 'guest') {
          await fetch(`/api/sessions/${encodeURIComponent(editingSession.id)}/refresh-rate`, {
            method: 'PATCH',
            headers: {
              'Content-Type': 'application/json',
              ...ApiService.getHeaders()
            },
            body: JSON.stringify({ refreshRate: editingPreferences.refreshRate || '' })
          });
        }

        setEditingSession(null);
        setEditingPreferences(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to save preferences');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to save preferences');
    } finally {
      setSavingPreferences(false);
    }
  };

  const formatTimeRemaining = (expiresAt: string) => {
    const now = new Date();
    const expiry = new Date(expiresAt);
    const diff = expiry.getTime() - now.getTime();

    if (diff <= 0) return 'Expired';

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

    if (session.id === currentDeviceId && isLocallyActive) {
      return 'active';
    }

    if (!session.lastSeenAt) return 'inactive';

    const now = new Date();
    const lastSeen = new Date(session.lastSeenAt);
    const diffSeconds = (now.getTime() - lastSeen.getTime()) / 1000;

    if (diffSeconds <= 60) return 'active';
    if (diffSeconds <= 600) return 'away'; // 10 minutes
    return 'inactive';
  };

  const renderSessionCard = (session: Session) => {
    const isExpanded = expandedSessions.has(session.id);
    const sessionStatus = getSessionStatus(session);
    const isActive = sessionStatus === 'active';
    const isAway = sessionStatus === 'away';
    const isDimmed = session.isExpired || session.isRevoked;
    const prefs = sessionPreferences[session.id];
    const isLoadingPrefs = loadingSessionPrefs.has(session.id);

    if (!prefs && !isLoadingPrefs && !session.isRevoked && !session.isExpired) {
      setTimeout(() => loadSessionPreferences(session.id), 0);
    }

    const themeName = prefs?.selectedTheme
      ? availableThemes.find((t) => t.id === prefs.selectedTheme)?.name || prefs.selectedTheme
      : 'Default';
    const timezoneLabel = prefs?.useLocalTimezone ? 'Local' : 'Server';

    return (
      <div key={session.id} className={`session-card ${isDimmed ? 'dimmed' : ''}`}>
        {/* Header - Always visible */}
        <div className="p-3 sm:p-4">
          {/* Mobile: Clickable header to expand */}
          <div
            className="sm:hidden cursor-pointer"
            onClick={() => toggleSessionExpanded(session.id)}
          >
            <div className="flex items-start gap-3">
              <div className="relative">
                <div
                  className="session-avatar"
                  style={{
                    backgroundColor: session.type === 'authenticated'
                      ? 'var(--theme-user-session-bg)'
                      : 'var(--theme-guest-session-bg)'
                  }}
                >
                  <User
                    className="w-5 h-5"
                    style={{
                      color: session.type === 'authenticated'
                        ? 'var(--theme-user-session)'
                        : 'var(--theme-guest-session)'
                    }}
                  />
                </div>
                {(isActive || isAway) && <div className={`status-dot ${isActive ? 'active' : 'away'} absolute -bottom-0.5 -right-0.5`} />}
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap mb-1">
                  <h3
                    className="font-semibold truncate text-sm"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    {session.deviceName || 'Unknown Device'}
                  </h3>
                  <span
                    className="px-1.5 py-0.5 text-[10px] rounded-full font-medium flex-shrink-0"
                    style={{
                      backgroundColor:
                        session.type === 'authenticated'
                          ? 'var(--theme-user-session-bg)'
                          : 'var(--theme-guest-session-bg)',
                      color:
                        session.type === 'authenticated'
                          ? 'var(--theme-user-session)'
                          : 'var(--theme-guest-session)'
                    }}
                  >
                    {session.type === 'authenticated' ? 'USER' : 'GUEST'}
                  </span>
                </div>
                <p className="text-xs truncate" style={{ color: 'var(--theme-text-muted)' }}>
                  {[session.browser, session.operatingSystem].filter(Boolean).join(' · ') ||
                    'Unknown device'}
                </p>
                <div className="flex items-center gap-1.5 mt-1.5 flex-wrap">
                  {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium"
                      style={{
                        backgroundColor: 'var(--theme-warning-bg)',
                        color: 'var(--theme-warning-text)'
                      }}
                    >
                      {formatTimeRemaining(session.expiresAt)}
                    </span>
                  )}
                  {session.isRevoked && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium"
                      style={{
                        backgroundColor: 'var(--theme-error-bg)',
                        color: 'var(--theme-error-text)'
                      }}
                    >
                      Revoked
                    </span>
                  )}
                  {session.isExpired && !session.isRevoked && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium"
                      style={{
                        backgroundColor: 'var(--theme-warning-bg)',
                        color: 'var(--theme-warning-text)'
                      }}
                    >
                      Expired
                    </span>
                  )}
                  {!session.isRevoked && !session.isExpired && prefs && (
                    <>
                      <span className="pref-badge text-[10px]">{themeName}</span>
                      <span className="pref-badge text-[10px]">{timezoneLabel} TZ</span>
                    </>
                  )}
                </div>
              </div>
              <ChevronDown
                className={`w-5 h-5 flex-shrink-0 transition-transform duration-200 ${isExpanded ? 'rotate-180' : ''}`}
                style={{ color: 'var(--theme-text-muted)' }}
              />
            </div>
          </div>

          {/* Desktop: Full layout always visible */}
          <div className="hidden sm:block">
            <div className="flex items-start justify-between gap-4">
              <div className="flex items-start gap-3 flex-1 min-w-0">
                <div className="relative">
                  <div
                    className="session-avatar"
                    style={{
                      backgroundColor: session.type === 'authenticated'
                        ? 'var(--theme-user-session-bg)'
                        : 'var(--theme-guest-session-bg)'
                    }}
                  >
                    <User
                      className="w-5 h-5"
                      style={{
                        color: session.type === 'authenticated'
                          ? 'var(--theme-user-session)'
                          : 'var(--theme-guest-session)'
                      }}
                    />
                  </div>
                  {(isActive || isAway) && (
                    <div className={`status-dot ${isActive ? 'active' : 'away'} absolute -bottom-0.5 -right-0.5`} />
                  )}
                </div>
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap mb-2">
                    <h3
                      className="font-semibold truncate"
                      style={{ color: 'var(--theme-text-primary)' }}
                    >
                      {session.deviceName || 'Unknown Device'}
                    </h3>
                    <span
                      className="px-2 py-0.5 text-xs rounded-full font-medium"
                      style={{
                        backgroundColor:
                          session.type === 'authenticated'
                            ? 'var(--theme-user-session-bg)'
                            : 'var(--theme-guest-session-bg)',
                        color:
                          session.type === 'authenticated'
                            ? 'var(--theme-user-session)'
                            : 'var(--theme-guest-session)'
                      }}
                    >
                      {session.type === 'authenticated' ? 'USER' : 'GUEST'}
                    </span>
                    {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                      <span
                        className="px-2 py-0.5 text-xs rounded-full font-medium"
                        style={{
                          backgroundColor: 'var(--theme-warning-bg)',
                          color: 'var(--theme-warning-text)'
                        }}
                      >
                        {formatTimeRemaining(session.expiresAt)}
                      </span>
                    )}
                    {session.isRevoked && (
                      <span
                        className="px-2 py-0.5 text-xs rounded-full font-medium"
                        style={{
                          backgroundColor: 'var(--theme-error-bg)',
                          color: 'var(--theme-error-text)'
                        }}
                      >
                        Revoked
                      </span>
                    )}
                    {session.isExpired && !session.isRevoked && (
                      <span
                        className="px-2 py-0.5 text-xs rounded-full font-medium"
                        style={{
                          backgroundColor: 'var(--theme-warning-bg)',
                          color: 'var(--theme-warning-text)'
                        }}
                      >
                        Expired
                      </span>
                    )}
                    {!session.isRevoked && !session.isExpired && prefs && (
                      <>
                        <span className="pref-badge">{themeName}</span>
                        <span className="pref-badge">{timezoneLabel} TZ</span>
                      </>
                    )}
                  </div>

                  <div className="grid grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-2 text-sm">
                    {session.ipAddress && (
                      <div
                        className="flex items-center gap-2"
                        style={{ color: 'var(--theme-text-secondary)' }}
                      >
                        <Network className="w-4 h-4 flex-shrink-0" />
                        <ClientIpDisplay
                          clientIp={cleanIpAddress(session.ipAddress)}
                          className="truncate"
                        />
                      </div>
                    )}
                    {session.operatingSystem && (
                      <div
                        className="flex items-center gap-2"
                        style={{ color: 'var(--theme-text-secondary)' }}
                      >
                        <Monitor className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">{session.operatingSystem}</span>
                      </div>
                    )}
                    {session.browser && (
                      <div
                        className="flex items-center gap-2"
                        style={{ color: 'var(--theme-text-secondary)' }}
                      >
                        <Globe className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">{session.browser}</span>
                      </div>
                    )}
                    <div
                      className="flex items-center gap-2"
                      style={{ color: 'var(--theme-text-secondary)' }}
                    >
                      <Clock className="w-4 h-4 flex-shrink-0" />
                      <span className="truncate">
                        Created: <FormattedTimestamp timestamp={session.createdAt} />
                      </span>
                    </div>
                    {session.lastSeenAt && (
                      <div
                        className="flex items-center gap-2"
                        style={{ color: 'var(--theme-text-secondary)' }}
                      >
                        <Clock className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          Last seen: <FormattedTimestamp timestamp={session.lastSeenAt} />
                        </span>
                      </div>
                    )}
                    {session.revokedAt && session.type === 'guest' && (
                      <div
                        className="flex items-center gap-2"
                        style={{ color: 'var(--theme-error-text)' }}
                      >
                        <Clock className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          Revoked: <FormattedTimestamp timestamp={session.revokedAt} />
                        </span>
                      </div>
                    )}
                    {session.revokedBy && session.type === 'guest' && (
                      <div
                        className="flex items-center gap-2"
                        style={{ color: 'var(--theme-text-secondary)' }}
                      >
                        <User className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          Revoked by:{' '}
                          <ClientIpDisplay clientIp={cleanIpAddress(session.revokedBy)} />
                        </span>
                      </div>
                    )}
                  </div>

                  <div
                    className="text-xs font-mono truncate mt-2 pt-2 border-t"
                    style={{
                      color: 'var(--theme-text-muted)',
                      borderColor: 'var(--theme-border-secondary)'
                    }}
                  >
                    Device ID: {session.deviceId || session.id}
                  </div>
                </div>
              </div>

              {/* Desktop action buttons */}
              <div className="flex gap-2 items-start flex-shrink-0">
                <Button
                  variant="default"
                  color="blue"
                  size="sm"
                  leftSection={<Edit className="w-4 h-4" />}
                  onClick={() => handleEditSession(session)}
                >
                  Edit
                </Button>
                {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                  <Button
                    variant="default"
                    color="orange"
                    size="sm"
                    onClick={() => handleRevokeSession(session)}
                    disabled={revokingSession === session.id}
                  >
                    {revokingSession === session.id ? 'Revoking...' : 'Revoke'}
                  </Button>
                )}
                <Button
                  variant="default"
                  color="red"
                  size="sm"
                  leftSection={<Trash2 className="w-4 h-4" />}
                  onClick={() => handleDeleteSession(session)}
                  disabled={deletingSession === session.id}
                  style={
                    isDimmed
                      ? {
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: 'var(--theme-error)'
                        }
                      : undefined
                  }
                >
                  {deletingSession === session.id ? 'Deleting...' : 'Delete'}
                </Button>
              </div>
            </div>
          </div>
        </div>

        {/* Mobile expanded content */}
        <div
          className={`sm:hidden overflow-hidden transition-all duration-200 ${isExpanded ? 'max-h-[500px] opacity-100' : 'max-h-0 opacity-0'}`}
        >
          <div
            className="px-3 pb-3 space-y-3"
            style={{
              borderTop: '1px solid var(--theme-border-secondary)',
              opacity: isDimmed ? 0.6 : 1
            }}
          >
            <div className="space-y-2.5 pt-3">
              {session.ipAddress && (
                <div>
                  <div
                    className="flex items-center gap-1.5 text-[10px] mb-0.5"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    <Network className="w-3 h-3" />
                    <span>IP Address</span>
                  </div>
                  <div
                    className="text-sm font-medium pl-[18px]"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} />
                  </div>
                </div>
              )}
              {session.operatingSystem && (
                <div>
                  <div
                    className="flex items-center gap-1.5 text-[10px] mb-0.5"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    <Monitor className="w-3 h-3" />
                    <span>Operating System</span>
                  </div>
                  <div
                    className="text-sm font-medium pl-[18px]"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    {session.operatingSystem}
                  </div>
                </div>
              )}
              {session.browser && (
                <div>
                  <div
                    className="flex items-center gap-1.5 text-[10px] mb-0.5"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    <Globe className="w-3 h-3" />
                    <span>Browser</span>
                  </div>
                  <div
                    className="text-sm font-medium pl-[18px]"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    {session.browser}
                  </div>
                </div>
              )}
              <div>
                <div
                  className="flex items-center gap-1.5 text-[10px] mb-0.5"
                  style={{ color: 'var(--theme-text-muted)' }}
                >
                  <Clock className="w-3 h-3" />
                  <span>Created</span>
                </div>
                <div
                  className="text-sm font-medium pl-[18px]"
                  style={{ color: 'var(--theme-text-primary)' }}
                >
                  <FormattedTimestamp timestamp={session.createdAt} />
                </div>
              </div>
              {session.lastSeenAt && (
                <div>
                  <div
                    className="flex items-center gap-1.5 text-[10px] mb-0.5"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    <Clock className="w-3 h-3" />
                    <span>Last Seen</span>
                  </div>
                  <div
                    className="text-sm font-medium pl-[18px]"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    <FormattedTimestamp timestamp={session.lastSeenAt} />
                  </div>
                </div>
              )}
              {session.revokedAt && session.type === 'guest' && (
                <div>
                  <div
                    className="flex items-center gap-1.5 text-[10px] mb-0.5"
                    style={{ color: 'var(--theme-error-text)' }}
                  >
                    <Clock className="w-3 h-3" />
                    <span>Revoked</span>
                  </div>
                  <div
                    className="text-sm font-medium pl-[18px]"
                    style={{ color: 'var(--theme-error-text)' }}
                  >
                    <FormattedTimestamp timestamp={session.revokedAt} />
                  </div>
                </div>
              )}
              {session.revokedBy && session.type === 'guest' && (
                <div>
                  <div
                    className="flex items-center gap-1.5 text-[10px] mb-0.5"
                    style={{ color: 'var(--theme-text-muted)' }}
                  >
                    <User className="w-3 h-3" />
                    <span>Revoked By</span>
                  </div>
                  <div
                    className="text-sm font-medium pl-[18px]"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    <ClientIpDisplay clientIp={cleanIpAddress(session.revokedBy)} />
                  </div>
                </div>
              )}
              <div>
                <div
                  className="flex items-center gap-1.5 text-[10px] mb-0.5"
                  style={{ color: 'var(--theme-text-muted)' }}
                >
                  <span>Device ID</span>
                </div>
                <div
                  className="text-xs font-mono break-all"
                  style={{ color: 'var(--theme-text-secondary)' }}
                >
                  {session.deviceId || session.id}
                </div>
              </div>
            </div>

            {/* Mobile action buttons */}
            <div className="flex flex-col gap-2 pt-2">
              <Button
                variant="default"
                color="blue"
                size="sm"
                leftSection={<Edit className="w-4 h-4" />}
                onClick={(e) => {
                  e.stopPropagation();
                  handleEditSession(session);
                }}
                fullWidth
              >
                Edit Preferences
              </Button>
              {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                <Button
                  variant="default"
                  color="orange"
                  size="sm"
                  onClick={(e) => {
                    e.stopPropagation();
                    handleRevokeSession(session);
                  }}
                  disabled={revokingSession === session.id}
                  fullWidth
                >
                  {revokingSession === session.id ? 'Revoking...' : 'Revoke Session'}
                </Button>
              )}
              <Button
                variant="default"
                color="red"
                size="sm"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={(e) => {
                  e.stopPropagation();
                  handleDeleteSession(session);
                }}
                disabled={deletingSession === session.id}
                fullWidth
              >
                {deletingSession === session.id ? 'Deleting...' : 'Delete Session'}
              </Button>
            </div>
          </div>
        </div>
      </div>
    );
  };

  return (
    <>
      <Card padding="none">
        <div
          className="p-4 sm:p-5 border-b"
          style={{ borderColor: 'var(--theme-border-secondary)' }}
        >
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
                Active Sessions
              </h2>
              <HelpPopover
                width={300}
                sections={[
                  {
                    title: 'Session Types',
                    items: [
                      {
                        label: 'Authenticated',
                        description: 'Full access, no expiration',
                        color: 'var(--theme-user-session)'
                      },
                      {
                        label: 'Guest',
                        description: `Read-only for ${guestDurationHours} hours`,
                        color: 'var(--theme-guest-session)'
                      }
                    ]
                  },
                  {
                    title: 'Actions',
                    items: [
                      {
                        label: 'Revoke',
                        description: 'End a guest session immediately',
                        color: 'var(--theme-warning)'
                      },
                      {
                        label: 'Delete',
                        description: 'Remove device from history',
                        color: 'var(--theme-error)'
                      }
                    ]
                  }
                ]}
              />
            </div>

            <ToggleSwitch
              options={[
                { value: 'unlocked', label: 'Unlocked', icon: <Unlock />, activeColor: 'success' },
                { value: 'locked', label: 'Locked', icon: <Lock />, activeColor: 'error' }
              ]}
              value={guestModeLocked ? 'locked' : 'unlocked'}
              onChange={onToggleGuestLock}
              disabled={updatingGuestLock}
              loading={updatingGuestLock}
              title={
                guestModeLocked
                  ? 'Guest mode is locked - new guests cannot log in'
                  : 'Guest mode is unlocked - guests can log in'
              }
            />
          </div>
        </div>
        <div className="p-4 sm:p-5">
          {loading && (
            <div className="text-center py-12">
              <Loader2
                className="w-8 h-8 animate-spin mx-auto"
                style={{ color: 'var(--theme-primary)' }}
              />
              <p className="text-sm mt-3" style={{ color: 'var(--theme-text-muted)' }}>
                Loading sessions...
              </p>
            </div>
          )}

          {!loading && sessions.length === 0 && (
            <div className="text-center py-12">
              <div
                className="w-16 h-16 mx-auto mb-4 rounded-xl flex items-center justify-center"
                style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
              >
                <Users className="w-8 h-8" style={{ color: 'var(--theme-text-muted)' }} />
              </div>
              <p className="font-medium" style={{ color: 'var(--theme-text-secondary)' }}>
                No active sessions
              </p>
              <p className="text-sm mt-1" style={{ color: 'var(--theme-text-muted)' }}>
                Sessions will appear here when users connect
              </p>
            </div>
          )}

          {!loading && sessions.length > 0 && (
            <div className="space-y-2">{sessions.map(renderSessionCard)}</div>
          )}
        </div>

        {/* Pagination */}
        {!loading && totalPages > 1 && (
          <div
            className="px-4 sm:px-5 py-3 border-t"
            style={{ borderColor: 'var(--theme-border-secondary)' }}
          >
            <Pagination
              currentPage={currentPage}
              totalPages={totalPages}
              totalItems={totalCount}
              itemsPerPage={pageSize}
              onPageChange={(newPage) => {
                setCurrentPage(newPage);
                loadSessions(true, newPage);
              }}
              itemLabel="sessions"
              showCard={false}
            />
          </div>
        )}
      </Card>

      {/* Revoke Device Modal */}
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
            <span>Revoke Device</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to revoke this{' '}
            {pendingRevokeSession?.type === 'authenticated' ? 'authenticated user' : 'guest'}?
          </p>

          {pendingRevokeSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingRevokeSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                Device ID: {pendingRevokeSession.id}
              </p>
            </div>
          )}

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">What happens when you revoke:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>The device is marked as revoked but not deleted</li>
                <li>The user will be logged out immediately</li>
                <li>The device record remains in history</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingRevokeSession(null)}
              disabled={!!revokingSession}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={confirmRevokeSession}
              loading={!!revokingSession}
            >
              Revoke Device
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Device Modal */}
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
            <span>Delete Device</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to permanently delete this{' '}
            {pendingDeleteSession?.type === 'authenticated'
              ? 'authenticated device'
              : 'guest device'}
            ?
          </p>

          {pendingDeleteSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingDeleteSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                Device ID: {pendingDeleteSession.id}
              </p>
            </div>
          )}

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">Warning:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>The device will be permanently removed from history</li>
                <li>The user will be logged out immediately</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingDeleteSession(null)}
              disabled={!!deletingSession}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={confirmDeleteSession}
              loading={!!deletingSession}
            >
              Delete Permanently
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
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Edit className="w-6 h-6 text-themed-accent" />
            <span>Edit User Preferences</span>
          </div>
        }
        size="lg"
      >
        <div className="space-y-4">
          {editingSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {editingSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted">
                {editingSession.type === 'authenticated' ? 'Authenticated User' : 'Guest User'}
              </p>
              <p className="text-xs text-themed-muted font-mono">Device ID: {editingSession.id}</p>
            </div>
          )}

          {loadingPreferences && (
            <div className="text-center py-8">
              <Loader2
                className="w-8 h-8 animate-spin mx-auto"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p className="text-sm mt-2" style={{ color: 'var(--theme-text-secondary)' }}>
                Loading preferences...
              </p>
            </div>
          )}

          {!loadingPreferences && editingPreferences && (
            <div className="space-y-4">
              {/* Theme Selection */}
              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="text-sm font-medium text-themed-primary">
                    Selected Theme
                  </label>
                  {editingPreferences.selectedTheme ? (
                    <button
                      type="button"
                      onClick={() =>
                        setEditingPreferences({
                          ...editingPreferences,
                          selectedTheme: null
                        })
                      }
                      className="text-xs px-2 py-0.5 rounded transition-colors"
                      style={{
                        color: 'var(--theme-primary)',
                        backgroundColor: 'var(--theme-bg-tertiary)'
                      }}
                      onMouseEnter={(e) =>
                        (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
                      }
                      onMouseLeave={(e) =>
                        (e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)')
                      }
                    >
                      Use Default
                    </button>
                  ) : (
                    <span
                      className="text-xs px-2 py-0.5 rounded"
                      style={{
                        color: 'var(--theme-text-muted)',
                        backgroundColor: 'var(--theme-bg-tertiary)'
                      }}
                    >
                      Using Default
                    </span>
                  )}
                </div>
                <EnhancedDropdown
                  options={availableThemes.map((theme) => ({
                    value: theme.id,
                    label: theme.name
                  }))}
                  value={editingPreferences.selectedTheme || defaultGuestTheme}
                  onChange={(value) =>
                    setEditingPreferences({
                      ...editingPreferences,
                      selectedTheme: value
                    })
                  }
                  className="w-full"
                />
                <p className="text-xs text-themed-muted mt-1">
                  {editingPreferences.selectedTheme
                    ? 'Custom theme for this user'
                    : `Default: ${availableThemes.find((t) => t.id === defaultGuestTheme)?.name || defaultGuestTheme}`}
                </p>
              </div>

              {/* Refresh Rate (Guest Users Only) */}
              {editingSession && editingSession.type === 'guest' && (
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <label className="text-sm font-medium text-themed-primary">
                      Refresh Rate
                    </label>
                    {editingPreferences.refreshRate ? (
                      <button
                        type="button"
                        onClick={() =>
                          setEditingPreferences({
                            ...editingPreferences,
                            refreshRate: null
                          })
                        }
                        className="text-xs px-2 py-0.5 rounded transition-colors"
                        style={{
                          color: 'var(--theme-primary)',
                          backgroundColor: 'var(--theme-bg-tertiary)'
                        }}
                        onMouseEnter={(e) =>
                          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
                        }
                        onMouseLeave={(e) =>
                          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)')
                        }
                      >
                        Use Default
                      </button>
                    ) : (
                      <span
                        className="text-xs px-2 py-0.5 rounded"
                        style={{
                          color: 'var(--theme-text-muted)',
                          backgroundColor: 'var(--theme-bg-tertiary)'
                        }}
                      >
                        Using Default
                      </span>
                    )}
                  </div>
                  <EnhancedDropdown
                    options={refreshRateOptions}
                    value={editingPreferences.refreshRate || defaultGuestRefreshRate}
                    onChange={(value) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        refreshRate: value
                      })
                    }
                    className="w-full"
                  />
                  <p className="text-xs text-themed-muted mt-1">
                    {editingPreferences.refreshRate
                      ? 'Custom refresh rate for this user'
                      : `Default: ${refreshRateOptions.find((o) => o.value === defaultGuestRefreshRate)?.label || defaultGuestRefreshRate}`}
                  </p>
                </div>
              )}

              {/* UI Preferences */}
              <div className="space-y-3">
                <h4 className="text-sm font-medium text-themed-primary">UI Preferences</h4>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.sharpCorners}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        sharpCorners: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <span className="text-sm text-themed-secondary">Sharp Corners</span>
                </label>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={!editingPreferences.disableTooltips}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        disableTooltips: !e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <span className="text-sm text-themed-secondary">Tooltips</span>
                </label>

                {editingSession && editingSession.type === 'authenticated' && (
                  <>
                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={!editingPreferences.disableStickyNotifications}
                        onChange={(e) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            disableStickyNotifications: !e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded"
                        style={{ accentColor: 'var(--theme-primary)' }}
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">Sticky Notifications</span>
                        <span className="text-xs text-themed-muted">
                          Keep notification bar fixed at top when scrolling
                        </span>
                      </div>
                    </label>

                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={editingPreferences.picsAlwaysVisible}
                        onChange={(e) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            picsAlwaysVisible: e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded"
                        style={{ accentColor: 'var(--theme-primary)' }}
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">Static Notifications</span>
                        <span className="text-xs text-themed-muted">
                          Require manual dismissal - won&apos;t auto-clear
                        </span>
                      </div>
                    </label>
                  </>
                )}

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.showDatasourceLabels}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        showDatasourceLabels: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <div className="flex flex-col">
                    <span className="text-sm text-themed-secondary">Datasource Labels</span>
                    <span className="text-xs text-themed-muted">
                      Show datasource indicators on downloads (multi-datasource mode)
                    </span>
                  </div>
                </label>
              </div>

              {/* Date & Time Preferences */}
              <div className="space-y-3">
                <h4 className="text-sm font-medium text-themed-primary">Date & Time</h4>

                {/* Allowed Time Formats Multi-Select */}
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <label className="text-sm text-themed-secondary">
                      Allowed Time Formats
                    </label>
                    {editingPreferences.allowedTimeFormats ? (
                      <button
                        type="button"
                        onClick={() =>
                          setEditingPreferences({
                            ...editingPreferences,
                            allowedTimeFormats: undefined
                          })
                        }
                        className="text-xs px-2 py-0.5 rounded transition-colors"
                        style={{
                          color: 'var(--theme-primary)',
                          backgroundColor: 'var(--theme-bg-tertiary)'
                        }}
                        onMouseEnter={(e) =>
                          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-secondary)')
                        }
                        onMouseLeave={(e) =>
                          (e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)')
                        }
                      >
                        Use Default
                      </button>
                    ) : (
                      <span
                        className="text-xs px-2 py-0.5 rounded"
                        style={{
                          color: 'var(--theme-text-muted)',
                          backgroundColor: 'var(--theme-bg-tertiary)'
                        }}
                      >
                        Using Default
                      </span>
                    )}
                  </div>
                  <MultiSelectDropdown
                    options={timeFormatOptions.map((opt) => ({
                      value: opt.value,
                      label: opt.label,
                      description: opt.description,
                      icon: opt.icon
                    }))}
                    values={
                      editingPreferences.allowedTimeFormats ??
                      defaultGuestPrefs.allowedTimeFormats ??
                      ['server-24h', 'server-12h', 'local-24h', 'local-12h']
                    }
                    onChange={(formats) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        allowedTimeFormats: formats
                      })
                    }
                    placeholder="Select allowed formats"
                    minSelections={1}
                  />
                  <p className="text-xs text-themed-muted mt-1">
                    {editingPreferences.allowedTimeFormats
                      ? 'Custom formats for this user'
                      : `Using default (${
                          defaultGuestPrefs.allowedTimeFormats?.length === 4
                            ? 'All formats'
                            : defaultGuestPrefs.allowedTimeFormats
                                ?.map((f) => timeFormatOptions.find((o) => o.value === f)?.label)
                                .join(', ') || 'All formats'
                        })`}
                  </p>
                </div>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.showYearInDates}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        showYearInDates: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <div className="flex flex-col">
                    <span className="text-sm text-themed-secondary">Always Show Year</span>
                    <span className="text-xs text-themed-muted">
                      Include year in dates even for the current year
                    </span>
                  </div>
                </label>
              </div>
            </div>
          )}

          <div
            className="flex justify-end space-x-3 pt-4 border-t"
            style={{ borderColor: 'var(--theme-border-secondary)' }}
          >
            <Button
              variant="default"
              onClick={() => {
                setEditingSession(null);
                setEditingPreferences(null);
              }}
              disabled={savingPreferences}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="blue"
              onClick={handleSavePreferences}
              loading={savingPreferences}
              disabled={loadingPreferences}
            >
              Save Preferences
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default ActiveSessions;
