import React, { useState, useEffect } from 'react';
import TimeFilter from '../common/TimeFilter';
import RefreshRateSelector from '../common/RefreshRateSelector';
import TimezoneSelector from '../common/TimezoneSelector';
import { Tooltip } from '@components/ui/Tooltip';
import LancacheIcon from '../ui/LancacheIcon';
import { useMockMode } from '@contexts/MockModeContext';
import { useAuth } from '@contexts/AuthContext';
import authService from '@services/auth.service';

interface HeaderProps {
  title?: string;
  subtitle?: string;
  connectionStatus?: 'connected' | 'disconnected' | 'reconnecting';
}

const Header: React.FC<HeaderProps> = ({
  title = 'LANCache Manager',
  subtitle = 'High-performance cache monitoring & management',
  connectionStatus = 'connected'
}) => {
  const { mockMode } = useMockMode();
  const { authMode } = useAuth();
  const [isGuestMode, setIsGuestMode] = useState(false);
  const [isRevoked, setIsRevoked] = useState(false);
  const [deviceId, setDeviceId] = useState('');

  // Event-driven updates from AuthContext - no polling needed
  useEffect(() => {
    const guestMode = authMode === 'guest';
    const expired = authMode === 'expired';
    setIsGuestMode(guestMode || expired);
    setIsRevoked(expired);
    if (guestMode || expired) {
      setDeviceId(authService.getGuestSessionId() || '');
    }
  }, [authMode]);

  return (
    <>
      <style>{`
        @keyframes float-bounce {
          0%, 100% {
            transform: translateY(0px);
          }
          50% {
            transform: translateY(-4px);
          }
        }
        @keyframes shadow-pulse {
          0%, 100% {
            transform: scale(1);
            opacity: 0.3;
          }
          50% {
            transform: scale(0.85);
            opacity: 0.15;
          }
        }
      `}</style>
      <header className="border-b bg-themed-nav border-themed-nav">
        <div className="container mx-auto px-4">
          {/* Desktop: Single row layout */}
          <div className="hidden md:flex items-center justify-between h-16 min-w-0">
            <div className="flex items-center space-x-2 sm:space-x-3 min-w-0 flex-1">
              <div className="p-1.5 sm:p-2 rounded-lg flex-shrink-0 bg-themed-tertiary relative">
                <div className="relative w-9 h-9 flex items-center justify-center">
                  <LancacheIcon
                    className="flex-shrink-0 relative z-[1]"
                    size={36}
                    style={{ animation: 'float-bounce 2.5s ease-in-out infinite' }}
                  />
                  {/* Static shadow below the floating icon */}
                  <div
                    className="absolute bottom-0 left-1 w-7 h-1.5 rounded-full pointer-events-none z-0"
                    style={{
                      background: 'radial-gradient(ellipse at center, color-mix(in srgb, var(--theme-text-primary) 60%, transparent) 0%, color-mix(in srgb, var(--theme-text-primary) 30%, transparent) 40%, transparent 70%)',
                      animation: 'shadow-pulse 2.5s ease-in-out infinite'
                    }}
                  />
                </div>
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <h1 className="text-lg sm:text-xl font-bold text-themed-primary truncate">
                    {title}
                  </h1>
                  {connectionStatus === 'connected' && (
                    <Tooltip content="API Status: Connected - All backend services are responding normally">
                      <div className="flex items-center">
                        <div className="w-2 h-2 rounded-full flex-shrink-0 bg-[var(--theme-success)]"></div>
                      </div>
                    </Tooltip>
                  )}
                  {connectionStatus === 'disconnected' && (
                    <Tooltip content="API Status: Disconnected - Unable to connect to backend services">
                      <div className="flex items-center gap-1 text-themed-error">
                        <div className="w-2 h-2 rounded-full flex-shrink-0 bg-[var(--theme-error)]"></div>
                        <span className="text-xs">Disconnected</span>
                      </div>
                    </Tooltip>
                  )}
                  {connectionStatus === 'reconnecting' && (
                    <Tooltip content="API Status: Reconnecting - Attempting to restore connection to backend services">
                      <div className="flex items-center gap-1 text-themed-warning">
                        <div className="w-2 h-2 rounded-full animate-pulse flex-shrink-0 bg-[var(--theme-warning)]"></div>
                        <span className="text-xs">Reconnecting</span>
                      </div>
                    </Tooltip>
                  )}
                </div>
                <div className="flex items-center gap-2 text-xs sm:text-sm text-themed-muted">
                  <span className="truncate">{subtitle}</span>
                  {isGuestMode && (
                    <div
                      className="px-2 py-0.5 rounded text-xs font-medium flex flex-col sm:flex-row sm:items-center gap-0.5 sm:gap-1.5"
                      style={{
                        backgroundColor: isRevoked
                          ? 'var(--theme-error-bg)'
                          : 'var(--theme-warning-bg)',
                        color: isRevoked ? 'var(--theme-error-text)' : 'var(--theme-warning-text)',
                        border: isRevoked
                          ? '1px solid var(--theme-error)'
                          : '1px solid var(--theme-warning)'
                      }}
                    >
                      <span className="whitespace-nowrap">{isRevoked ? 'Revoked' : 'Guest Mode'}</span>
                      {!isRevoked && (
                        <>
                          <span className="hidden sm:inline opacity-50 text-[0.9em]">•</span>
                          <span className="truncate max-w-[180px] sm:max-w-none font-mono text-[0.8em] opacity-85">
                            {deviceId}
                          </span>
                        </>
                      )}
                    </div>
                  )}
                </div>
              </div>
            </div>

            <div className="flex items-center gap-2 sm:gap-3 flex-shrink-0">
              <TimezoneSelector />
              <RefreshRateSelector disabled={mockMode} />
              <TimeFilter disabled={mockMode} />
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
                      className="flex-shrink-0 relative z-[1]"
                      size={36}
                      style={{ animation: 'float-bounce 2.5s ease-in-out infinite' }}
                    />
                    <div
                      className="absolute bottom-0 left-1 w-7 h-1.5 rounded-full pointer-events-none z-0"
                      style={{
                        background: 'radial-gradient(ellipse at center, color-mix(in srgb, var(--theme-text-primary) 60%, transparent) 0%, color-mix(in srgb, var(--theme-text-primary) 30%, transparent) 40%, transparent 70%)',
                        animation: 'shadow-pulse 2.5s ease-in-out infinite'
                      }}
                    />
                  </div>
                </div>
                {/* Status indicator */}
                <div
                  className={`absolute -bottom-0.5 -right-0.5 w-2.5 h-2.5 rounded-full border-2 border-[var(--theme-nav-bg)] z-10 ${connectionStatus === 'reconnecting' ? 'animate-pulse' : ''}`}
                  style={{
                    backgroundColor: connectionStatus === 'connected'
                      ? 'var(--theme-success)'
                      : connectionStatus === 'disconnected'
                      ? 'var(--theme-error)'
                      : 'var(--theme-warning)'
                  }}
                />
              </div>

              {/* Right: Controls in a row */}
              <div className="flex items-center gap-1.5">
                <TimezoneSelector />
                <RefreshRateSelector disabled={mockMode} />
                <TimeFilter disabled={mockMode} />
              </div>
            </div>

            {/* Guest Mode Pill - Mobile (below controls if present) */}
            {isGuestMode && (
              <div className="flex justify-center mt-1.5">
                <div
                  className="px-2 py-0.5 rounded text-xs font-medium flex items-center gap-1"
                  style={{
                    backgroundColor: isRevoked
                      ? 'var(--theme-error-bg)'
                      : 'var(--theme-warning-bg)',
                    color: isRevoked ? 'var(--theme-error-text)' : 'var(--theme-warning-text)',
                    border: isRevoked
                      ? '1px solid var(--theme-error)'
                      : '1px solid var(--theme-warning)'
                  }}
                >
                  <span className="whitespace-nowrap">{isRevoked ? 'Revoked' : 'Guest'}</span>
                  {!isRevoked && (
                    <>
                      <span className="opacity-50">•</span>
                      <span className="font-mono text-[0.75em] opacity-85">
                        {deviceId}
                      </span>
                    </>
                  )}
                </div>
              </div>
            )}
          </div>
        </div>
      </header>
    </>
  );
};

export default Header;
