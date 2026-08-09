import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import TimeFilter from '../common/TimeFilter';
import RefreshRateSelector from '../common/RefreshRateSelector';
import TimezoneSelector from '../common/TimezoneSelector';
import LanguageSelector from '../common/LanguageSelector';
import GitHubProjectsDropdown from '../common/GitHubProjectsDropdown';
import ThemePreviewBanner from '../ui/ThemePreviewBanner';
import { Tooltip } from '@components/ui/Tooltip';
import Badge from '@components/ui/Badge';
import LancacheIcon from '../ui/LancacheIcon';
import LoadingSpinner from '../common/LoadingSpinner';
import { useMockMode } from '@contexts/useMockMode';
import { useAuth } from '@contexts/useAuth';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { formatSessionTimeRemaining } from '@utils/timeFormatters';

interface HeaderProps {
  title?: string;
  subtitle?: string;
  connectionStatus?: 'connected' | 'disconnected' | 'reconnecting';
}

const Header: React.FC<HeaderProps> = ({ title, subtitle, connectionStatus = 'connected' }) => {
  const { t } = useTranslation();
  const { mockMode } = useMockMode();
  const { authMode, sessionExpiresAt } = useAuth();
  const { isRefreshing, dataStale } = useStats();
  const showStaleData = connectionStatus === 'connected' && dataStale;
  const isGuestMode = authMode === 'guest';
  const [timeRemaining, setTimeRemaining] = useState<string | null>(null);

  // Update countdown every 30 seconds for guest sessions
  useEffect(() => {
    if (!isGuestMode || !sessionExpiresAt) {
      setTimeRemaining(null);
      return;
    }

    setTimeRemaining(formatSessionTimeRemaining(sessionExpiresAt));
    const interval = setInterval(() => {
      setTimeRemaining(formatSessionTimeRemaining(sessionExpiresAt));
    }, 30_000);

    return () => clearInterval(interval);
  }, [isGuestMode, sessionExpiresAt]);

  const resolvedTitle = title ?? t('app.title');
  const resolvedSubtitle = subtitle ?? t('app.subtitle');

  return (
    <>
      <header className="border-b bg-themed-nav border-themed-nav">
        <div className="container mx-auto px-4">
          {/* Desktop: Single row layout */}
          <div className="hidden md:flex items-center justify-between h-16 min-w-0">
            <div className="flex items-center space-x-2 sm:space-x-3 min-w-0 flex-1">
              <div className="p-1.5 sm:p-2 rounded-lg flex-shrink-0 bg-themed-tertiary relative">
                <div className="relative w-9 h-9 flex items-center justify-center">
                  <LancacheIcon
                    className="flex-shrink-0 relative z-[1] header-float-bounce"
                    size={36}
                  />
                  {/* Static shadow below the floating icon */}
                  <div className="absolute bottom-0 left-1 w-7 h-1.5 rounded-full pointer-events-none z-0 header-icon-shadow header-shadow-pulse" />
                </div>
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <h1 className="text-lg sm:text-xl font-bold text-themed-primary truncate">
                    {resolvedTitle}
                  </h1>
                  {connectionStatus === 'connected' && !showStaleData && (
                    <Tooltip content={t('status.connectedTooltip')}>
                      <div className="flex items-center">
                        <div className="w-2 h-2 rounded-full flex-shrink-0 bg-[var(--theme-success)]"></div>
                      </div>
                    </Tooltip>
                  )}
                  {showStaleData && (
                    <Tooltip content={t('status.staleDataTooltip')}>
                      <div className="flex items-center">
                        <div className="w-2 h-2 rounded-full flex-shrink-0 bg-[var(--theme-warning)]"></div>
                      </div>
                    </Tooltip>
                  )}
                  {connectionStatus === 'disconnected' && (
                    <Tooltip content={t('status.disconnectedTooltip')}>
                      <div className="flex items-center gap-1 text-themed-error">
                        <div className="w-2 h-2 rounded-full flex-shrink-0 bg-[var(--theme-error)]"></div>
                        <span className="text-xs">{t('status.disconnectedLabel')}</span>
                      </div>
                    </Tooltip>
                  )}
                  {connectionStatus === 'reconnecting' && (
                    <Tooltip content={t('status.reconnectingTooltip')}>
                      <div className="flex items-center gap-1 text-themed-warning">
                        <div className="w-2 h-2 rounded-full animate-pulse flex-shrink-0 bg-[var(--theme-warning)]"></div>
                        <span className="text-xs">{t('status.reconnectingLabel')}</span>
                      </div>
                    </Tooltip>
                  )}
                </div>
                <div className="flex items-center gap-2 text-xs sm:text-sm text-themed-muted">
                  <span className="truncate">{resolvedSubtitle}</span>
                  {isGuestMode && (
                    <Badge variant="warning">
                      {t('guest.guestMode')}
                      {timeRemaining ? ` \u00B7 ${timeRemaining}` : ''}
                    </Badge>
                  )}
                </div>
              </div>
            </div>

            <div className="header-controls flex items-center gap-2 sm:gap-3 flex-shrink-0">
              <ThemePreviewBanner />
              <GitHubProjectsDropdown />
              <LanguageSelector />
              <TimezoneSelector />
              <RefreshRateSelector disabled={mockMode} />
              <TimeFilter disabled={mockMode} />
              {isRefreshing && <LoadingSpinner inline size="xs" />}
            </div>
          </div>

          {/* Mobile: Compact single row layout */}
          <div className="md:hidden py-2">
            <div className="flex items-center justify-between gap-2">
              {/* Left: Icon with status indicator */}
              <div className="relative flex-shrink-0">
                <div className="p-1.5 rounded-lg flex items-center justify-center bg-themed-tertiary">
                  <div className="relative w-9 h-9 flex items-center justify-center">
                    <LancacheIcon
                      className="flex-shrink-0 relative z-[1] header-float-bounce"
                      size={36}
                    />
                    <div className="absolute bottom-0 left-1 w-7 h-1.5 rounded-full pointer-events-none z-0 header-icon-shadow header-shadow-pulse" />
                  </div>
                </div>
                {/* Status indicator */}
                <div
                  className={`absolute -bottom-0.5 -right-0.5 w-2.5 h-2.5 rounded-full connection-dot connection-dot--${connectionStatus} ${showStaleData ? 'connection-dot--stale' : ''} z-10 ${connectionStatus === 'reconnecting' ? 'animate-pulse' : ''}`}
                />
              </div>

              {/* Right: Controls in a row - constrained width to prevent overflow */}
              <div className="header-mobile-controls flex items-center gap-0.5 xs:gap-1 flex-1 justify-end min-w-0">
                <ThemePreviewBanner iconOnly={true} />
                <GitHubProjectsDropdown iconOnly={true} />
                <LanguageSelector iconOnly={true} />
                <TimezoneSelector iconOnly={true} />
                <RefreshRateSelector disabled={mockMode} iconOnly={true} />
                <TimeFilter disabled={mockMode} iconOnly={true} />
                {isRefreshing && <LoadingSpinner inline size="xs" />}
              </div>
            </div>

            {/* Guest Mode Pill - Mobile (below controls if present) */}
            {isGuestMode && (
              <div className="flex justify-center mt-1.5">
                <Badge variant="warning">
                  {t('guest.guest')}
                  {timeRemaining ? ` \u00B7 ${timeRemaining}` : ''}
                </Badge>
              </div>
            )}
          </div>
        </div>
      </header>
    </>
  );
};

export default Header;
