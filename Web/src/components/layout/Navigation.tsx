import React, { useState } from 'react';
import { LayoutDashboard, Download, Users, Settings, Menu, X } from 'lucide-react';

interface NavigationProps {
  activeTab: string;
  setActiveTab: (tab: string) => void;
}

const Navigation: React.FC<NavigationProps> = ({ activeTab, setActiveTab }) => {
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  const tabs = [
    { id: 'dashboard', label: 'Dashboard', icon: LayoutDashboard },
    { id: 'downloads', label: 'Downloads', icon: Download },
    { id: 'clients', label: 'Clients', icon: Users },
    { id: 'management', label: 'Management', icon: Settings }
  ];

  const TabButton: React.FC<{
    tab: (typeof tabs)[0];
    isActive: boolean;
    onClick: () => void;
    className?: string;
  }> = ({ tab, isActive, onClick, className = '' }) => {
    const Icon = tab.icon;

    return (
      <button
        onClick={onClick}
        className={`flex items-center space-x-2 px-3 py-2 rounded-lg font-medium transition-all duration-200 ${className}`}
        style={{
          color: isActive ? 'var(--theme-nav-tab-active)' : 'var(--theme-nav-tab-inactive)',
          backgroundColor: 'transparent'
        }}
        onMouseEnter={(e) => {
          if (!isActive) {
            e.currentTarget.style.color = 'var(--theme-nav-tab-hover)';
            e.currentTarget.style.backgroundColor = 'var(--theme-nav-mobile-item-hover)';
          }
        }}
        onMouseLeave={(e) => {
          if (!isActive) {
            e.currentTarget.style.color = 'var(--theme-nav-tab-inactive)';
            e.currentTarget.style.backgroundColor = 'transparent';
          }
        }}
      >
        <Icon className="w-5 h-5" />
        <span>{tab.label}</span>
        {isActive && (
          <div
            className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full"
            style={{ backgroundColor: 'var(--theme-nav-tab-active-border)' }}
          />
        )}
      </button>
    );
  };

  return (
    <nav
      className="border-b"
      style={{
        backgroundColor: 'var(--theme-nav-bg)',
        borderColor: 'var(--theme-nav-border)'
      }}
    >
      <div className="container mx-auto px-4">
        {/* Desktop Navigation */}
        <div className="hidden md:flex space-x-1 h-12 items-center">
          {tabs.map((tab) => (
            <div key={tab.id} className="relative">
              <TabButton
                tab={tab}
                isActive={activeTab === tab.id}
                onClick={() => setActiveTab(tab.id)}
              />
            </div>
          ))}
        </div>

        {/* Mobile Navigation */}
        <div className="md:hidden">
          <div className="flex items-center justify-between h-12">
            <div className="flex items-center space-x-2">
              <span className="font-medium text-themed-primary">
                {tabs.find((t) => t.id === activeTab)?.label || 'Dashboard'}
              </span>
            </div>
            <button
              onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
              className="p-2 rounded-lg transition-colors"
              style={{
                color: 'var(--theme-nav-tab-inactive)',
                backgroundColor: 'transparent'
              }}
              onMouseEnter={(e) => {
                e.currentTarget.style.backgroundColor = 'var(--theme-nav-mobile-item-hover)';
              }}
              onMouseLeave={(e) => {
                e.currentTarget.style.backgroundColor = 'transparent';
              }}
            >
              {mobileMenuOpen ? <X className="w-5 h-5" /> : <Menu className="w-5 h-5" />}
            </button>
          </div>

          {/* Mobile Menu */}
          {mobileMenuOpen && (
            <div
              className="border-t py-2 space-y-1"
              style={{
                backgroundColor: 'var(--theme-nav-mobile-menu-bg)',
                borderColor: 'var(--theme-nav-border)'
              }}
            >
              {tabs.map((tab) => (
                <TabButton
                  key={tab.id}
                  tab={tab}
                  isActive={activeTab === tab.id}
                  onClick={() => {
                    setActiveTab(tab.id);
                    setMobileMenuOpen(false);
                  }}
                  className="w-full justify-start"
                />
              ))}
            </div>
          )}
        </div>
      </div>
    </nav>
  );
};

export default Navigation;
