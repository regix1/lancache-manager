import React, { useState, useEffect } from 'react';
import TimeFilter from '../common/TimeFilter';
import PollingRateSelector from '../common/PollingRateSelector';
import { Tooltip } from '@components/ui/Tooltip';
import LancacheIcon from '../ui/LancacheIcon';
import { useMockMode } from '@contexts/MockModeContext';
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
  const [isGuestMode, setIsGuestMode] = useState(false);
  const [isRevoked, setIsRevoked] = useState(false);
  const [deviceId, setDeviceId] = useState('');

  useEffect(() => {
    // Check if user is in guest mode or expired
    const checkGuestMode = () => {
      const guestMode = authService.authMode === 'guest';
      const expired = authService.authMode === 'expired';
      setIsGuestMode(guestMode || expired);
      setIsRevoked(expired);
      if (guestMode || expired) {
        setDeviceId(authService.getGuestSessionId() || '');
      }
    };

    checkGuestMode();
    // Recheck periodically in case auth mode changes
    const interval = setInterval(checkGuestMode, 1000);
    return () => clearInterval(interval);
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
          <div className="flex items-center justify-between h-16 min-w-0">
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
                <h1 className="text-lg sm:text-xl font-bold text-themed-primary truncate">
                  {title}
                </h1>
                <div className="flex items-center gap-2 text-xs sm:text-sm text-themed-muted hidden sm:flex">
                  <span className="truncate">{subtitle}</span>
                  {isGuestMode && (
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
                      <span>{isRevoked ? 'Revoked' : 'Guest Mode'}</span>
                      {!isRevoked && (
                        <>
                          <span style={{ opacity: 0.7 }}>|</span>
                          <span style={{ fontFamily: 'monospace', fontSize: '0.85em' }}>
                            {deviceId}
                          </span>
                        </>
                      )}
                    </div>
                  )}
                  {connectionStatus === 'connected' && (
                    <Tooltip content="API Status: Connected - All backend services are responding normally">
                      <div className="flex items-center">
                        <div
                          className="w-1.5 h-1.5 rounded-full"
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
                          className="w-1.5 h-1.5 rounded-full"
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
                          className="w-1.5 h-1.5 rounded-full animate-pulse"
                          style={{ backgroundColor: 'var(--theme-warning)' }}
                        ></div>
                        <span className="text-xs">Reconnecting</span>
                      </div>
                    </Tooltip>
                  )}
                </div>
              </div>
            </div>

            <div className="flex items-center gap-2 sm:gap-3 flex-shrink-0">
              <PollingRateSelector disabled={mockMode} />
              <TimeFilter disabled={mockMode} />
            </div>
          </div>
        </div>
      </header>
    </>
  );
};

export default Header;
