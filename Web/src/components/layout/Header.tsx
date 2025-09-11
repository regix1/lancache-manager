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
        <div className="flex items-center justify-between h-16">
          <div className="flex items-center space-x-3">
            <div className="p-2 rounded-lg" style={{ backgroundColor: 'var(--theme-icon-blue)' }}>
              <Monitor className="w-6 h-6 text-white" />
            </div>
            <div>
              <h1 className="text-xl font-bold text-themed-primary">{title}</h1>
              <p className="text-sm text-themed-muted">{subtitle}</p>
            </div>
          </div>

          <div className="flex items-center space-x-2">
            <div className={`flex items-center space-x-1 ${status.color}`}>
              {status.icon}
              <span className="text-sm font-medium">{status.text}</span>
            </div>
          </div>
        </div>
      </div>
    </header>
  );
};

export default Header;
