import React, { useState, useEffect, useRef } from 'react';
import TimeFilter from '../common/TimeFilter';
import PollingRateSelector from '../common/PollingRateSelector';
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
  const [pollingButtonWidth, setPollingButtonWidth] = useState<number | null>(null);
  const pollingButtonRef = useRef<HTMLDivElement>(null);

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

  // Measure polling button width with resize observer
  useEffect(() => {
    if (!pollingButtonRef.current) return;

    const measureWidth = () => {
      if (pollingButtonRef.current) {
        const width = pollingButtonRef.current.offsetWidth;
        setPollingButtonWidth(width);
      }
    };

    // Initial measurement
    measureWidth();

    // Create resize observer to re-measure on layout changes
    const resizeObserver = new ResizeObserver(() => {
      measureWidth();
    });

    resizeObserver.observe(pollingButtonRef.current);

    // Also listen for window resize
    window.addEventListener('resize', measureWidth);

    return () => {
      resizeObserver.disconnect();
      window.removeEventListener('resize', measureWidth);
    };
  }, []);

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
      <header
        className="border-b"
        style={{
          backgroundColor: 'var(--theme-nav-bg)',
          borderColor: 'var(--theme-nav-border)'
        }}
      >
        <div className="container mx-auto px-4">
          {/* Desktop: Single row layout */}
          <div className="hidden md:flex items-center justify-between h-16 min-w-0">
            <div className="flex items-center space-x-2 sm:space-x-3 min-w-0 flex-1">
              <div
                className="p-1.5 sm:p-2 rounded-lg flex-shrink-0"
                style={{
                  backgroundColor: 'var(--theme-bg-tertiary)',
                  position: 'relative'
                }}
              >
                <div
                  style={{
                    position: 'relative',
                    width: '36px',
                    height: '36px',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center'
                  }}
                >
                  <LancacheIcon
                    className="flex-shrink-0"
                    size={36}
                    style={{
                      animation: 'float-bounce 2.5s ease-in-out infinite',
                      position: 'relative',
                      zIndex: 1
                    }}
                  />
                  {/* Static shadow below the floating icon */}
                  <div
                    style={{
                      position: 'absolute',
                      bottom: '0px',
                      left: '4px',
                      width: '28px',
                      height: '6px',
                      background:
                        'radial-gradient(ellipse at center, rgba(0, 0, 0, 0.6) 0%, rgba(0, 0, 0, 0.3) 40%, rgba(0, 0, 0, 0) 70%)',
                      borderRadius: '50%',
                      animation: 'shadow-pulse 2.5s ease-in-out infinite',
                      pointerEvents: 'none',
                      zIndex: 0
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
                        <div
                          className="w-2 h-2 rounded-full flex-shrink-0"
                          style={{ backgroundColor: 'var(--theme-success)' }}
                        ></div>
                      </div>
                    </Tooltip>
                  )}
                  {connectionStatus === 'disconnected' && (
                    <Tooltip content="API Status: Disconnected - Unable to connect to backend services">
                      <div
                        className="flex items-center gap-1"
                        style={{ color: 'var(--theme-error-text)' }}
                      >
                        <div
                          className="w-2 h-2 rounded-full flex-shrink-0"
                          style={{ backgroundColor: 'var(--theme-error)' }}
                        ></div>
                        <span className="text-xs">Disconnected</span>
                      </div>
                    </Tooltip>
                  )}
                  {connectionStatus === 'reconnecting' && (
                    <Tooltip content="API Status: Reconnecting - Attempting to restore connection to backend services">
                      <div
                        className="flex items-center gap-1"
                        style={{ color: 'var(--theme-warning-text)' }}
                      >
                        <div
                          className="w-2 h-2 rounded-full animate-pulse flex-shrink-0"
                          style={{ backgroundColor: 'var(--theme-warning)' }}
                        ></div>
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
                          <span className="hidden sm:inline" style={{ opacity: 0.5, fontSize: '0.9em' }}>•</span>
                          <span
                            className="truncate max-w-[180px] sm:max-w-none"
                            style={{ fontFamily: 'monospace', fontSize: '0.8em', opacity: 0.85 }}
                          >
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
              <PollingRateSelector disabled={mockMode} />
              <TimeFilter disabled={mockMode} />
            </div>
          </div>

          {/* Mobile: Compact single row layout */}
          <div className="md:hidden py-2">
            <div className="flex items-center justify-between gap-2">
              {/* Left: Icon with status indicator */}
              <div style={{ position: 'relative' }} className="flex-shrink-0">
                <div
                  className="p-1.5 rounded-lg flex items-center justify-center"
                  style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                >
                  <div
                    style={{
                      position: 'relative',
                      width: '36px',
                      height: '36px',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center'
                    }}
                  >
                    <LancacheIcon
                      className="flex-shrink-0"
                      size={36}
                      style={{
                        animation: 'float-bounce 2.5s ease-in-out infinite',
                        position: 'relative',
                        zIndex: 1
                      }}
                    />
                    <div
                      style={{
                        position: 'absolute',
                        bottom: '0px',
                        left: '4px',
                        width: '28px',
                        height: '6px',
                        background:
                          'radial-gradient(ellipse at center, rgba(0, 0, 0, 0.6) 0%, rgba(0, 0, 0, 0.3) 40%, rgba(0, 0, 0, 0) 70%)',
                        borderRadius: '50%',
                        animation: 'shadow-pulse 2.5s ease-in-out infinite',
                        pointerEvents: 'none',
                        zIndex: 0
                      }}
                    />
                  </div>
                </div>
                {/* Status indicator */}
                <div
                  style={{
                    position: 'absolute',
                    bottom: '-2px',
                    right: '-2px',
                    width: '10px',
                    height: '10px',
                    borderRadius: '50%',
                    backgroundColor: connectionStatus === 'connected'
                      ? 'var(--theme-success)'
                      : connectionStatus === 'disconnected'
                      ? 'var(--theme-error)'
                      : 'var(--theme-warning)',
                    border: '2px solid var(--theme-nav-bg)',
                    zIndex: 10,
                    ...(connectionStatus === 'reconnecting' && {
                      animation: 'pulse 2s cubic-bezier(0.4, 0, 0.6, 1) infinite'
                    })
                  }}
                />
              </div>

              {/* Right: Controls in a row */}
              <div className="flex items-center gap-1.5">
                <TimezoneSelector />
                <div ref={pollingButtonRef}>
                  <PollingRateSelector disabled={mockMode} />
                </div>
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
                      <span style={{ opacity: 0.5 }}>•</span>
                      <span
                        className="truncate max-w-[120px]"
                        style={{ fontFamily: 'monospace', fontSize: '0.75em', opacity: 0.85 }}
                      >
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
