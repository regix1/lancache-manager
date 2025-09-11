import React from 'react';
import { Monitor, Wifi } from 'lucide-react';

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
  const getStatusInfo = () => {
    switch (connectionStatus) {
      case 'connected':
        return {
          color: 'cache-hit',
          text: 'Connected',
          icon: <Wifi className="w-4 h-4" />
        };
      case 'disconnected':
        return {
          color: 'text-themed-error',
          text: 'Disconnected',
          icon: <Wifi className="w-4 h-4" />
        };
      case 'reconnecting':
        return {
          color: 'cache-miss',
          text: 'Reconnecting...',
          icon: <Wifi className="w-4 h-4 animate-pulse" />
        };
      default:
        return {
          color: 'text-themed-muted',
          text: 'Unknown',
          icon: <Wifi className="w-4 h-4" />
        };
    }
  };

  const status = getStatusInfo();

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
              <p className="text-xs sm:text-sm text-themed-muted truncate hidden sm:block">{subtitle}</p>
            </div>
          </div>

          <div className="flex items-center space-x-1 sm:space-x-2 flex-shrink-0">
            <div className={`flex items-center space-x-1 ${status.color}`}>
              {status.icon}
              <span className="text-xs sm:text-sm font-medium hidden sm:inline">{status.text}</span>
            </div>
          </div>
        </div>
      </div>
    </header>
  );
};

export default Header;
