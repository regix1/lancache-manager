import React from 'react';
import { Monitor } from 'lucide-react';
import TimeFilter from '../common/TimeFilter';
import Tooltip from '../ui/Tooltip';

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

  return (
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
            <div className="p-1.5 sm:p-2 rounded-lg flex-shrink-0" style={{ backgroundColor: 'var(--theme-icon-blue)' }}>
              <Monitor className="w-5 h-5 sm:w-6 sm:h-6 text-white" />
            </div>
            <div className="min-w-0 flex-1">
              <h1 className="text-lg sm:text-xl font-bold text-themed-primary truncate">{title}</h1>
              <div className="flex items-center gap-2 text-xs sm:text-sm text-themed-muted hidden sm:flex">
                <span className="truncate">{subtitle}</span>
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
                    <div className="flex items-center gap-1" style={{ color: 'var(--theme-error-text)' }}>
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
                    <div className="flex items-center gap-1" style={{ color: 'var(--theme-warning-text)' }}>
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
            <TimeFilter />
          </div>
        </div>
      </div>
    </header>
  );
};

export default Header;
