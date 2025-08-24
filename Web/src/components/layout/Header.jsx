import React from 'react';
import { Server, Activity } from 'lucide-react';
import { useData } from '../../contexts/DataContext';

const Header = () => {
  const { loading, mockMode } = useData();

  return (
    <header className="bg-gray-800 border-b border-gray-700">
      <div className="container mx-auto px-4 py-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-3">
            <Server className="w-8 h-8 text-blue-500" />
            <h1 className="text-2xl font-bold">LanCache Monitor</h1>
          </div>
          <div className="flex items-center space-x-4">
            {mockMode && (
              <span className="px-3 py-1 bg-blue-900 text-blue-300 rounded-lg text-sm">
                Mock Mode
              </span>
            )}
            <div className="flex items-center space-x-2">
              <Activity className={`w-4 h-4 ${loading ? 'text-yellow-500 animate-pulse' : 'text-green-500'}`} />
              <span className="text-sm text-gray-400">
                {loading ? 'Updating...' : 'Connected'}
              </span>
            </div>
          </div>
        </div>
      </div>
    </header>
  );
};

export default Header;