import React, { useState, useMemo, useRef, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { LayoutDashboard, Download, Laptop, Settings, Menu, Users, Key, ChevronDown, CalendarDays, Terminal } from 'lucide-react';
import type { AuthMode } from '@services/auth.service';

interface NavigationProps {
  activeTab: string;
  setActiveTab: (tab: string) => void;
  authMode?: AuthMode;
  prefillEnabled?: boolean;
  isBanned?: boolean;
}

const Navigation: React.FC<NavigationProps> = React.memo(
  ({ activeTab, setActiveTab, authMode = 'unauthenticated', prefillEnabled = false, isBanned = false }) => {
    const { t } = useTranslation();
    const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
    const [menuHeight, setMenuHeight] = useState(0);
    const menuContentRef = useRef<HTMLDivElement>(null);

    // Measure menu content height for smooth animation
    useEffect(() => {
      if (menuContentRef.current) {
        setMenuHeight(menuContentRef.current.scrollHeight);
      }
    }, [authMode, mobileMenuOpen]); // Recalculate when tabs change or menu opens

    const allTabs = [
      { id: 'dashboard', label: t('nav.dashboard'), icon: LayoutDashboard, requiresAuth: false, guestOnly: false, guestOrder: 1, authOrder: 1 },
      { id: 'downloads', label: t('nav.downloads'), icon: Download, requiresAuth: false, guestOnly: false, guestOrder: 2, authOrder: 2 },
      { id: 'clients', label: t('nav.clients'), icon: Laptop, requiresAuth: false, guestOnly: false, guestOrder: 3, authOrder: 3 },
      { id: 'prefill', label: t('nav.prefill'), icon: Terminal, requiresAuth: false, guestOnly: false, requiresPrefill: true, guestOrder: 4, authOrder: 7 },
      { id: 'authenticate', label: t('nav.authenticate'), icon: Key, requiresAuth: false, guestOnly: true, guestOrder: 5, authOrder: 0 },
      { id: 'users', label: t('nav.users'), icon: Users, requiresAuth: true, guestOnly: false, guestOrder: 0, authOrder: 4 },
      { id: 'events', label: t('nav.events'), icon: CalendarDays, requiresAuth: true, guestOnly: false, guestOrder: 0, authOrder: 5 },
      { id: 'management', label: t('nav.management'), icon: Settings, requiresAuth: true, guestOnly: false, guestOrder: 0, authOrder: 8 }
    ];

    // Filter and sort tabs based on authentication and prefill permission
    const tabs = useMemo(() => {
      const filtered = allTabs.filter((tab) => {
        // Show auth-required tabs only to authenticated users
        if (tab.requiresAuth) {
          return authMode === 'authenticated';
        }
        // Show guest-only tabs only to guests
        if (tab.guestOnly) {
          return authMode === 'guest';
        }
        // Prefill tab: show to authenticated users OR guests with prefill permission, but hide if banned
        if ('requiresPrefill' in tab && tab.requiresPrefill) {
          if (isBanned) return false;
          return authMode === 'authenticated' || prefillEnabled;
        }
        // Show public tabs to everyone
        return true;
      });
      
      // Sort by appropriate order based on auth mode
      const orderKey = authMode === 'authenticated' ? 'authOrder' : 'guestOrder';
      return filtered.sort((a, b) => a[orderKey] - b[orderKey]);
    }, [authMode, prefillEnabled, isBanned, t]);

    const TabButton: React.FC<{
      tab: (typeof tabs)[0];
      isActive: boolean;
      onClick: () => void;
      className?: string;
    }> = React.memo(
      ({ tab, isActive, onClick, className = '' }) => {
        const Icon = tab.icon;

        return (
          <button
            onClick={onClick}
            className={`flex items-center space-x-2 px-3 py-2 rounded-lg font-medium transition-all duration-200 bg-transparent ${className}`}
            style={{ color: isActive ? 'var(--theme-nav-tab-active)' : 'var(--theme-nav-tab-inactive)' }}
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
                className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full bg-themed-nav-tab-active-border"
              />
            )}
          </button>
        );
      },
      (prevProps, nextProps) => {
        // Only re-render if isActive state changes for this specific button
        return prevProps.isActive === nextProps.isActive && prevProps.tab.id === nextProps.tab.id;
      }
    );

    return (
      <nav className="border-b sticky top-0 z-50 md:relative bg-themed-nav border-themed-nav">
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
                  {tabs.find((t) => t.id === activeTab)?.label || t('nav.dashboard')}
                </span>
              </div>
              <button
                onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
                className="p-2 rounded-lg transition-colors flex items-center gap-1 bg-transparent text-[var(--theme-nav-tab-inactive)] hover:bg-[var(--theme-nav-mobile-item-hover)]"
              >
                <Menu className="w-5 h-5" />
                <ChevronDown
                  className={`w-4 h-4 transition-transform duration-300 ease-out ${mobileMenuOpen ? 'rotate-180' : 'rotate-0'}`}
                />
              </button>
            </div>

            {/* Mobile Menu - Animated */}
            <div
              className="overflow-hidden transition-all duration-300 ease-out"
              style={{
                maxHeight: mobileMenuOpen ? `${menuHeight}px` : '0px',
                opacity: mobileMenuOpen ? 1 : 0
              }}
            >
              <div
                ref={menuContentRef}
                className="border-t py-2 space-y-1 bg-themed-nav-mobile border-themed-nav"
              >
                {tabs.map((tab, index) => (
                  <div
                    key={tab.id}
                    className="transition-all duration-300 ease-out"
                    style={{
                      opacity: mobileMenuOpen ? 1 : 0,
                      transform: mobileMenuOpen ? 'translateX(0)' : 'translateX(-10px)',
                      transitionDelay: mobileMenuOpen ? `${index * 50}ms` : '0ms'
                    }}
                  >
                    <TabButton
                      tab={tab}
                      isActive={activeTab === tab.id}
                      onClick={() => {
                        setActiveTab(tab.id);
                        setMobileMenuOpen(false);
                      }}
                      className="w-full justify-start"
                    />
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </nav>
    );
  },
  (prevProps, nextProps) => {
    // Only re-render if activeTab, authMode, or prefillEnabled changes
    return prevProps.activeTab === nextProps.activeTab &&
           prevProps.authMode === nextProps.authMode &&
           prevProps.prefillEnabled === nextProps.prefillEnabled;
  }
);

Navigation.displayName = 'Navigation';

export default Navigation;
