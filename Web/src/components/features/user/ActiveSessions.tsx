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
  Monitor,
  Globe,
  MapPin,
  Edit,
  Lock,
  Unlock,
  ChevronDown,
  Download,
  Palette
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
import { useSessionPreferences } from '@contexts/SessionPreferencesContext';
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
  const { t } = useTranslation();
  const { refreshAuth } = useAuth();
  const { on, off } = useSignalR();
  const { prefs: defaultGuestPrefs } = useDefaultGuestPreferences();
  
  // Use centralized session preferences from context
  const { 
    getSessionPreferences, 
    loadSessionPreferences, 
    isLoaded: isPreferencesLoaded,
    isLoading: isPreferencesLoading
  } = useSessionPreferences();

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
  const [revokingSession, setRevokingSession] = useState<string | null>(null);
  const [expandedSessions, setExpandedSessions] = useState<Set<string>>(new Set());
  const { isActive: isLocallyActive } = useActivityTracker();
  const currentDeviceId = authService.getDeviceId();

  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);
  const [editingSession, setEditingSession] = useState<Session | null>(null);
  const [editingPreferences, setEditingPreferences] = useState<UserPreferences | null>(null);
  const [pendingPrefillChange, setPendingPrefillChange] = useState<boolean | null>(null);
  const [loadingPreferences, setLoadingPreferences] = useState(false);
  const [savingPreferences, setSavingPreferences] = useState(false);
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalPages, setTotalPages] = useState(1);
  const [totalCount, setTotalCount] = useState(0);

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

  useEffect(() => {
    loadSessions(true);

    on('UserSessionRevoked', handleSessionRevoked);
    on('UserSessionsCleared', handleSessionsCleared);
    on('UserSessionCreated', handleSessionCreated);
    on('SessionLastSeenUpdated', handleSessionLastSeenUpdated);

    return () => {
      off('UserSessionRevoked', handleSessionRevoked);
      off('UserSessionsCleared', handleSessionsCleared);
      off('UserSessionCreated', handleSessionCreated);
      off('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
    };
  }, [
    loadSessions,
    on,
    off,
    handleSessionRevoked,
    handleSessionsCleared,
    handleSessionCreated,
    handleSessionLastSeenUpdated
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

      const response = await fetch(endpoint, ApiService.getFetchOptions({
        method: 'DELETE'
      }));

      if (response.ok) {
        if (isOwnSession) {
          setPendingRevokeSession(null);
          showToast('info', t('activeSessions.info.revokedOwnSession'));

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
        showToast('error', errorData.message || errorData.error || t('activeSessions.errors.revokeSession'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.revokeSession'));
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

      const response = await fetch(endpoint, ApiService.getFetchOptions({
        method: 'DELETE'
      }));

      if (response.ok) {
        if (isOwnSession) {
          setPendingDeleteSession(null);
          showToast('info', t('activeSessions.info.deletedOwnSession'));

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
        showToast('error', errorData.message || errorData.error || t('activeSessions.errors.deleteSession'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('activeSessions.errors.deleteSession'));
    } finally {
      setDeletingSession(null);
    }
  };

  const handleEditSession = async (session: Session) => {
    setEditingSession(session);
    setPendingPrefillChange(null);
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
        }

        if (editingSession.type === 'guest') {
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

          // Handle prefill access change if pending
          if (pendingPrefillChange !== null) {
            const prefillResponse = await fetch(
              `/api/auth/guest/prefill/toggle/${encodeURIComponent(editingSession.deviceId || editingSession.id)}`,
              ApiService.getFetchOptions({
                method: 'POST',
                headers: {
                  'Content-Type': 'application/json'
                },
                body: JSON.stringify({ enabled: pendingPrefillChange })
              })
            );

            if (prefillResponse.ok) {
              const prefillData = await prefillResponse.json();
              // Update the session in the list
              setSessions((prev) =>
                prev.map((s) => {
                  if (s.id === editingSession.id) {
                    return {
                      ...s,
                      prefillEnabled: prefillData.prefillEnabled,
                      prefillExpiresAt: prefillData.prefillExpiresAt,
                      isPrefillExpired: false
                    };
                  }
                  return s;
                })
              );
            }
          }
        }

        // The session preferences will be automatically updated via SignalR
        // through the SessionPreferencesContext, no need to update local state

        setEditingSession(null);
        setEditingPreferences(null);
        setPendingPrefillChange(null);
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

  const formatTimeRemaining = (expiresAt: string) => {
    const now = new Date();
    const expiry = new Date(expiresAt);
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
    
    // Get preferences from centralized context
    const prefs = getSessionPreferences(session.id);
    const isLoadingPrefs = isPreferencesLoading(session.id);

    // Trigger load if not loaded and session is active
    if (!prefs && !isLoadingPrefs && !isPreferencesLoaded(session.id) && !session.isRevoked && !session.isExpired) {
      // Use setTimeout to avoid state update during render
      setTimeout(() => loadSessionPreferences(session.id), 0);
    }

    const themeName = prefs?.selectedTheme
      ? availableThemes.find((t) => t.id === prefs.selectedTheme)?.name || prefs.selectedTheme
      : t('activeSessions.preferencesModal.defaultThemeShort');
    const timezoneLabel = prefs?.useLocalTimezone
      ? t('activeSessions.labels.local')
      : t('activeSessions.labels.server');

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
                  className={`session-avatar ${
                    session.type === 'authenticated' ? 'session-badge-user' : 'session-badge-guest'
                  }`}
                >
                  <User
                    className={`w-5 h-5 ${
                      session.type === 'authenticated' ? 'user-session-icon' : 'guest-session-icon'
                    }`}
                  />
                </div>
                {(isActive || isAway) && <div className={`status-dot ${isActive ? 'active' : 'away'} absolute -bottom-0.5 -right-0.5`} />}
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap mb-1">
                  <h3 className="font-semibold truncate text-sm text-themed-primary">
                    {session.deviceName || t('activeSessions.unknownDevice')}
                  </h3>
                  <span
                    className={`px-1.5 py-0.5 text-[10px] rounded-full font-medium flex-shrink-0 ${
                      session.type === 'authenticated' ? 'session-badge-user' : 'session-badge-guest'
                    }`}
                  >
                    {session.type === 'authenticated'
                      ? t('activeSessions.labels.userBadge')
                      : t('activeSessions.labels.guestBadge')}
                  </span>
                </div>
                <p className="text-xs truncate text-themed-muted">
                  {[session.browser, session.operatingSystem].filter(Boolean).join(' · ') ||
                    t('activeSessions.unknownDeviceLower')}
                </p>
                <div className="flex items-center gap-1.5 mt-1.5 flex-wrap">
                  {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
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

          {/* Desktop: Full layout always visible */}
          <div className="hidden sm:block">
            <div className="flex items-start justify-between gap-4">
              <div className="flex items-start gap-3 flex-1 min-w-0">
                <div className="relative">
                  <div
                    className={`session-avatar ${
                      session.type === 'authenticated' ? 'session-badge-user' : 'session-badge-guest'
                    }`}
                  >
                    <User
                      className={`w-5 h-5 ${
                        session.type === 'authenticated' ? 'user-session-icon' : 'guest-session-icon'
                      }`}
                    />
                  </div>
                  {(isActive || isAway) && (
                    <div className={`status-dot ${isActive ? 'active' : 'away'} absolute -bottom-0.5 -right-0.5`} />
                  )}
                </div>
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap mb-2">
                    <h3 className="font-semibold truncate text-themed-primary">
                      {session.deviceName || t('activeSessions.unknownDevice')}
                    </h3>
                    <span
                      className={`px-2 py-0.5 text-xs rounded-full font-medium ${
                        session.type === 'authenticated' ? 'session-badge-user' : 'session-badge-guest'
                      }`}
                    >
                      {session.type === 'authenticated'
                        ? t('activeSessions.labels.userBadge')
                        : t('activeSessions.labels.guestBadge')}
                    </span>
                    {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                      <span className="px-2 py-0.5 text-xs rounded-full font-medium status-badge-warning">
                        {formatTimeRemaining(session.expiresAt)}
                      </span>
                    )}
                    {session.isRevoked && (
                      <span className="px-2 py-0.5 text-xs rounded-full font-medium status-badge-error">
                        {t('activeSessions.status.revoked')}
                      </span>
                    )}
                    {session.isExpired && !session.isRevoked && (
                      <span className="px-2 py-0.5 text-xs rounded-full font-medium status-badge-warning">
                        {t('activeSessions.prefill.status.expired')}
                      </span>
                    )}
                    {!session.isRevoked && !session.isExpired && prefs && (
                      <>
                        <span className="pref-badge">
                          <Palette className="w-3 h-3" />
                          {themeName}
                        </span>
                        <span className="pref-badge">
                          <Globe className="w-3 h-3" />
                          {timezoneLabel}
                        </span>
                      </>
                    )}
                  </div>

                  <div className="grid grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-2 text-sm">
                    {session.ipAddress && (
                      <div className="flex items-center gap-2 text-themed-secondary">
                        <Network className="w-4 h-4 flex-shrink-0" />
                        <ClientIpDisplay
                          clientIp={cleanIpAddress(session.ipAddress)}
                          className="truncate"
                        />
                      </div>
                    )}
                    {session.operatingSystem && (
                      <div className="flex items-center gap-2 text-themed-secondary">
                        <Monitor className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">{session.operatingSystem}</span>
                      </div>
                    )}
                    {session.browser && (
                      <div className="flex items-center gap-2 text-themed-secondary">
                        <Globe className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">{session.browser}</span>
                      </div>
                    )}
                    <div className="flex items-center gap-2 text-themed-secondary">
                      <Clock className="w-4 h-4 flex-shrink-0" />
                      <span className="truncate">
                        {t('activeSessions.labels.created')} <FormattedTimestamp timestamp={session.createdAt} />
                      </span>
                    </div>
                    {session.lastSeenAt && (
                      <div className="flex items-center gap-2 text-themed-secondary">
                        <Clock className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          {t('activeSessions.labels.lastSeen')} <FormattedTimestamp timestamp={session.lastSeenAt} />
                        </span>
                      </div>
                    )}
                    {session.revokedAt && session.type === 'guest' && (
                      <div className="flex items-center gap-2 text-themed-error">
                        <Clock className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          {t('activeSessions.labels.revokedAt')} <FormattedTimestamp timestamp={session.revokedAt} />
                        </span>
                      </div>
                    )}
                    {session.revokedBy && session.type === 'guest' && (
                      <div className="flex items-center gap-2 text-themed-secondary">
                        <User className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          {t('activeSessions.labels.revokedBy')}{' '}
                          <ClientIpDisplay clientIp={cleanIpAddress(session.revokedBy)} />
                        </span>
                      </div>
                    )}
                  </div>

                  <div className="text-xs font-mono truncate mt-2 pt-2 border-t text-themed-muted border-themed-secondary">
                    {t('activeSessions.labels.deviceIdWithValue', { id: session.deviceId || session.id })}
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
                  {t('actions.edit')}
                </Button>
                {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
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
                  {deletingSession === session.id
                    ? t('activeSessions.actions.deleting')
                    : t('activeSessions.actions.delete')}
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
              {session.operatingSystem && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                    <Monitor className="w-3 h-3" />
                    <span>{t('activeSessions.labels.operatingSystem')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-primary">
                    {session.operatingSystem}
                  </div>
                </div>
              )}
              {session.browser && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                    <Globe className="w-3 h-3" />
                    <span>{t('activeSessions.labels.browser')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-primary">
                    {session.browser}
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
              {session.revokedAt && session.type === 'guest' && (
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
              {session.revokedBy && session.type === 'guest' && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
                    <User className="w-3 h-3" />
                    <span>{t('activeSessions.labels.revokedByShort')}</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px] text-themed-primary">
                    <ClientIpDisplay clientIp={cleanIpAddress(session.revokedBy)} />
                  </div>
                </div>
              )}
              <div>
                <div className="flex items-center gap-1.5 text-[10px] mb-0.5 text-themed-muted">
          <span>{t('activeSessions.labels.deviceId')}</span>
                </div>
                <div className="text-xs font-mono break-all text-themed-secondary">
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
                {t('activeSessions.actions.editPreferences')}
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
                  {revokingSession === session.id
                    ? t('activeSessions.actions.revoking')
                    : t('activeSessions.actions.revokeSession')}
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
                {deletingSession === session.id
                  ? t('activeSessions.actions.deleting')
                  : t('activeSessions.actions.deleteSession')}
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
        <div className="p-4 sm:p-5 border-b border-themed-secondary">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <h2 className="text-lg font-semibold text-themed-primary">
                {t('activeSessions.title')}
              </h2>
              <HelpPopover
                width={300}
                sections={[
                  {
                    title: t('activeSessions.help.sessionTypes.title'),
                    items: [
                      {
                        label: t('activeSessions.help.sessionTypes.authenticated.label'),
                        description: t('activeSessions.help.sessionTypes.authenticated.description'),
                        color: 'var(--theme-user-session)'
                      },
                      {
                        label: t('activeSessions.help.sessionTypes.guest.label'),
                        description: t('activeSessions.help.sessionTypes.guest.description', {
                          hours: guestDurationHours
                        }),
                        color: 'var(--theme-guest-session)'
                      }
                    ]
                  },
                  {
                    title: t('activeSessions.help.actions.title'),
                    items: [
                      {
                        label: t('activeSessions.help.actions.revoke.label'),
                        description: t('activeSessions.help.actions.revoke.description'),
                        color: 'var(--theme-warning)'
                      },
                      {
                        label: t('activeSessions.help.actions.delete.label'),
                        description: t('activeSessions.help.actions.delete.description'),
                        color: 'var(--theme-error)'
                      }
                    ]
                  }
                ]}
              />
            </div>

            <ToggleSwitch
              options={[
                { value: 'unlocked', label: t('activeSessions.toggle.unlocked'), icon: <Unlock />, activeColor: 'success' },
                { value: 'locked', label: t('activeSessions.toggle.locked'), icon: <Lock />, activeColor: 'error' }
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
        <div className="p-4 sm:p-5">
          {loading && (
            <div className="text-center py-12">
              <Loader2 className="w-8 h-8 animate-spin mx-auto text-themed-accent" />
              <p className="text-sm mt-3 text-themed-muted">
                {t('activeSessions.loading')}
              </p>
            </div>
          )}

          {!loading && sessions.length === 0 && (
            <div className="text-center py-12">
              <div className="w-16 h-16 mx-auto mb-4 rounded-xl flex items-center justify-center bg-themed-tertiary">
                <Users className="w-8 h-8 text-themed-muted" />
              </div>
              <p className="font-medium text-themed-secondary">
                {t('activeSessions.empty.title')}
              </p>
              <p className="text-sm mt-1 text-themed-muted">
                {t('activeSessions.empty.subtitle')}
              </p>
            </div>
          )}

          {!loading && sessions.length > 0 && (
            <div className="space-y-2">{sessions.map(renderSessionCard)}</div>
          )}
        </div>

        {/* Pagination */}
        {!loading && totalPages > 1 && (
          <div className="px-4 sm:px-5 py-3 border-t border-themed-secondary">
            <Pagination
              currentPage={currentPage}
              totalPages={totalPages}
              totalItems={totalCount}
              itemsPerPage={pageSize}
              onPageChange={(newPage) => {
                setCurrentPage(newPage);
                loadSessions(true, newPage);
              }}
              itemLabel={t('activeSessions.paginationLabel')}
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
            <span>{t('activeSessions.revokeModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('activeSessions.revokeModal.message', {
              type: pendingRevokeSession?.type === 'authenticated'
                ? t('activeSessions.sessionTypes.authenticatedUser')
                : t('activeSessions.sessionTypes.guestUser')
            })}
          </p>

          {pendingRevokeSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingRevokeSession.deviceName || t('activeSessions.unknownDevice')}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                {t('activeSessions.labels.deviceIdWithValue', { id: pendingRevokeSession.id })}
              </p>
            </div>
          )}

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('activeSessions.revokeModal.noteTitle')}</p>
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
            <span>{t('activeSessions.deleteModal.title')}</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('activeSessions.deleteModal.message', {
              type: pendingDeleteSession?.type === 'authenticated'
                ? t('activeSessions.sessionTypes.authenticatedDevice')
                : t('activeSessions.sessionTypes.guestDevice')
            })}
          </p>

          {pendingDeleteSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingDeleteSession.deviceName || t('activeSessions.unknownDevice')}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                {t('activeSessions.labels.deviceIdWithValue', { id: pendingDeleteSession.id })}
              </p>
            </div>
          )}

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">{t('activeSessions.deleteModal.noteTitle')}</p>
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

      {/* Edit User Preferences Modal */}
      <Modal
        opened={!!editingSession}
        onClose={() => {
          if (!savingPreferences) {
            setEditingSession(null);
            setEditingPreferences(null);
            setPendingPrefillChange(null);
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
                {editingSession.deviceName || t('activeSessions.unknownDevice')}
              </p>
              <p className="text-xs text-themed-muted">
                {editingSession.type === 'authenticated'
                  ? t('activeSessions.sessionTypes.authenticatedUser')
                  : t('activeSessions.sessionTypes.guestUser')}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                {t('activeSessions.labels.deviceIdWithValue', { id: editingSession.id })}
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
                  {editingPreferences.selectedTheme && editingPreferences.selectedTheme !== defaultGuestTheme ? (
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
                    ? t('activeSessions.preferencesModal.customTheme')
                    : t('activeSessions.preferencesModal.defaultTheme', {
                        theme: availableThemes.find((t) => t.id === defaultGuestTheme)?.name || defaultGuestTheme
                      })}
                </p>
              </div>

              {/* Refresh Rate (Guest Users Only) */}
              {editingSession && editingSession.type === 'guest' && (
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <label className="text-sm font-medium text-themed-primary">
                      {t('activeSessions.preferencesModal.refreshRate')}
                    </label>
                    {editingPreferences.refreshRate && editingPreferences.refreshRate !== defaultGuestRefreshRate ? (
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
                      ? t('activeSessions.preferencesModal.customRefreshRate')
                      : t('activeSessions.preferencesModal.defaultRefreshRate', {
                          rate: translatedRefreshRateOptions.find((o) => o.value === defaultGuestRefreshRate)?.label || defaultGuestRefreshRate
                        })}
                  </p>
                </div>
              )}

              {/* Prefill Access (Guest Users Only) */}
              {editingSession && editingSession.type === 'guest' && !editingSession.isRevoked && !editingSession.isExpired && (() => {
                // Determine effective prefill state (pending change takes precedence)
                const currentPrefillEnabled = editingSession.prefillEnabled && !editingSession.isPrefillExpired;
                const effectivePrefillEnabled = pendingPrefillChange !== null ? pendingPrefillChange : currentPrefillEnabled;
                const hasUnsavedChange = pendingPrefillChange !== null && pendingPrefillChange !== currentPrefillEnabled;

                return (
                  <div className="p-4 rounded-lg bg-themed-tertiary border border-themed-secondary">
                    <div className="flex items-center gap-2 mb-3">
                      <Download className="w-4 h-4 text-themed-accent" />
                      <h4 className="text-sm font-medium text-themed-primary">
                        {t('activeSessions.prefill.title')}
                      </h4>
                    </div>
                    <p className="text-xs text-themed-muted mb-3">
                      {t('activeSessions.prefill.subtitle')}
                    </p>
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        {effectivePrefillEnabled ? (
                          <>
                            <span className="px-2 py-1 text-xs rounded-full font-medium status-badge-success">
                              {t('activeSessions.prefill.status.enabled')}
                            </span>
                            {!hasUnsavedChange && editingSession.prefillExpiresAt && (
                              <span className="text-xs text-themed-muted">
                                {t('activeSessions.prefill.status.expires', {
                                  time: formatTimeRemaining(editingSession.prefillExpiresAt)
                                })}
                              </span>
                            )}
                          </>
                        ) : editingSession.isPrefillExpired && pendingPrefillChange === null ? (
                          <span className="px-2 py-1 text-xs rounded-full font-medium status-badge-warning">
                            {t('activeSessions.prefill.status.expired')}
                          </span>
                        ) : (
                          <span className="px-2 py-1 text-xs rounded-full font-medium bg-themed-secondary text-themed-muted">
                            {t('activeSessions.prefill.status.disabled')}
                          </span>
                        )}
                        {hasUnsavedChange && (
                          <span className="text-xs text-themed-accent italic">
                            ({t('common.unsaved')})
                          </span>
                        )}
                      </div>
                      <Button
                        variant="default"
                        color={effectivePrefillEnabled ? 'orange' : 'green'}
                        size="sm"
                        onClick={() => setPendingPrefillChange(!effectivePrefillEnabled)}
                      >
                        {effectivePrefillEnabled
                          ? t('activeSessions.prefill.actions.revoke')
                          : t('activeSessions.prefill.actions.grant')}
                      </Button>
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
                    onChange={(e) =>
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
                    onChange={(e) =>
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
                        onChange={(e) =>
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
                    onChange={(e) =>
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
                      // Compare current formats with default to determine if "Use Default" should be shown
                      const currentFormats = editingPreferences.allowedTimeFormats;
                      const defaultFormats = defaultGuestPrefs.allowedTimeFormats ?? ['server-24h', 'server-12h', 'local-24h', 'local-12h'];
                      const isUsingDefault = !currentFormats ||
                        (currentFormats.length === defaultFormats.length &&
                         currentFormats.every(f => defaultFormats.includes(f)));

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
                    placeholder={t('user.guest.timeFormats.placeholder')}
                    minSelections={1}
                    dropdownWidth="w-80"
                  />
                  <p className="text-xs text-themed-muted mt-1">
                    {editingPreferences.allowedTimeFormats
                      ? t('activeSessions.preferencesModal.customFormats')
                      : t('activeSessions.preferencesModal.defaultFormats', {
                          formats: defaultGuestPrefs.allowedTimeFormats?.length === 4
                            ? t('activeSessions.preferencesModal.allFormats')
                            : defaultGuestPrefs.allowedTimeFormats
                                ?.map((f) => timeFormatOptions.find((o) => o.value === f)?.label)
                                .join(', ') || t('activeSessions.preferencesModal.allFormats')
                        })}
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

          <div
            className="flex justify-end space-x-3 pt-4 border-t border-themed-secondary"
          >
            <Button
              variant="default"
              onClick={() => {
                setEditingSession(null);
                setEditingPreferences(null);
                setPendingPrefillChange(null);
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
    </>
  );
};

export default ActiveSessions;
