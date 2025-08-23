import React, { useState, useEffect } from 'react';
import { 
  HeroUIProvider,
  Navbar, 
  NavbarBrand, 
  NavbarContent, 
  NavbarItem,
  NavbarMenuToggle,
  NavbarMenu,
  NavbarMenuItem,
  Button,
  Link,
  Switch,
  Chip,
  useTheme
} from '@heroui/react';
import { 
  Home, Download, BarChart3, Settings, 
  Server, Wifi, WifiOff, Sun, Moon, Activity
} from 'lucide-react';
import Dashboard from './components/Dashboard';
import Downloads from './components/Downloads';
import Statistics from './components/Statistics';
import Management from './components/Management';
import StatusIndicator from './components/StatusIndicator';

function App() {
  const [activeTab, setActiveTab] = useState('dashboard');
  const [isMenuOpen, setIsMenuOpen] = useState(false);
  const [isOnline, setIsOnline] = useState(true);
  const [isDark, setIsDark] = useState(true);
  const { setTheme } = useTheme();

  useEffect(() => {
    // Check online status
    const checkOnline = () => {
      fetch('/api/health')
        .then(() => setIsOnline(true))
        .catch(() => setIsOnline(false));
    };
    
    checkOnline();
    const interval = setInterval(checkOnline, 30000);
    return () => clearInterval(interval);
  }, []);

  useEffect(() => {
    setTheme(isDark ? 'dark' : 'light');
  }, [isDark, setTheme]);

  const navigation = [
    { id: 'dashboard', name: 'Dashboard', icon: Home },
    { id: 'downloads', name: 'Downloads', icon: Download },
    { id: 'statistics', name: 'Statistics', icon: BarChart3 },
    { id: 'management', name: 'Management', icon: Settings },
  ];

  const renderContent = () => {
    switch (activeTab) {
      case 'dashboard':
        return <Dashboard />;
      case 'downloads':
        return <Downloads />;
      case 'statistics':
        return <Statistics />;
      case 'management':
        return <Management />;
      default:
        return <Dashboard />;
    }
  };

  return (
    <HeroUIProvider theme={isDark ? 'dark' : 'light'}>
      <div className="min-h-screen bg-background text-foreground">
        <Navbar 
          isMenuOpen={isMenuOpen}
          onMenuOpenChange={setIsMenuOpen}
          className="border-b border-divider"
          maxWidth="xl"
        >
          <NavbarContent>
            <NavbarMenuToggle
              aria-label={isMenuOpen ? "Close menu" : "Open menu"}
              className="sm:hidden"
            />
            <NavbarBrand>
              <div className="flex items-center gap-3">
                <div className="relative">
                  <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-gradient-to-br from-primary to-secondary shadow-lg">
                    <Server className="h-6 w-6 text-white" />
                  </div>
                  {isOnline && (
                    <div className="absolute -bottom-1 -right-1 h-3 w-3 animate-pulse rounded-full border-2 border-background bg-success"></div>
                  )}
                </div>
                <div>
                  <p className="font-bold text-xl bg-gradient-to-r from-primary to-secondary bg-clip-text text-transparent">
                    LanCache Manager
                  </p>
                  <p className="text-xs text-default-500">Network Cache Monitor</p>
                </div>
              </div>
            </NavbarBrand>
          </NavbarContent>

          <NavbarContent className="hidden sm:flex gap-4" justify="center">
            {navigation.map((item) => {
              const Icon = item.icon;
              const isActive = activeTab === item.id;
              return (
                <NavbarItem key={item.id} isActive={isActive}>
                  <Button
                    color={isActive ? "primary" : "default"}
                    variant={isActive ? "solid" : "light"}
                    startContent={<Icon className="h-4 w-4" />}
                    onPress={() => setActiveTab(item.id)}
                    className="font-medium"
                  >
                    {item.name}
                  </Button>
                </NavbarItem>
              );
            })}
          </NavbarContent>

          <NavbarContent justify="end">
            <NavbarItem>
              <Chip
                startContent={isOnline ? <Wifi className="h-3 w-3" /> : <WifiOff className="h-3 w-3" />}
                variant="flat"
                color={isOnline ? "success" : "danger"}
                size="sm"
                className="hidden sm:flex"
              >
                {isOnline ? "Connected" : "Offline"}
              </Chip>
            </NavbarItem>
            <NavbarItem>
              <Switch
                size="lg"
                color="primary"
                isSelected={isDark}
                onValueChange={setIsDark}
                thumbIcon={({ isSelected, className }) =>
                  isSelected ? (
                    <Moon className={className} />
                  ) : (
                    <Sun className={className} />
                  )
                }
              />
            </NavbarItem>
          </NavbarContent>

          <NavbarMenu>
            {navigation.map((item) => {
              const Icon = item.icon;
              const isActive = activeTab === item.id;
              return (
                <NavbarMenuItem key={item.id}>
                  <Button
                    className="w-full justify-start"
                    color={isActive ? "primary" : "default"}
                    variant={isActive ? "solid" : "light"}
                    startContent={<Icon className="h-5 w-5" />}
                    onPress={() => {
                      setActiveTab(item.id);
                      setIsMenuOpen(false);
                    }}
                  >
                    {item.name}
                  </Button>
                </NavbarMenuItem>
              );
            })}
          </NavbarMenu>
        </Navbar>

        {/* Main Content */}
        <main className="container mx-auto max-w-7xl px-6 py-8">
          {renderContent()}
        </main>

        {/* Footer */}
        <footer className="border-t border-divider mt-auto">
          <div className="container mx-auto max-w-7xl px-6 py-4">
            <div className="flex flex-col sm:flex-row justify-between items-center gap-4">
              <p className="text-sm text-default-500">
                © 2025 LanCache Manager • Optimizing your network cache
              </p>
              <div className="flex items-center gap-6">
                <Link
                  href="https://lancache.net"
                  target="_blank"
                  className="text-sm text-default-500 hover:text-primary flex items-center gap-1"
                >
                  <Activity className="h-3 w-3" />
                  LanCache.NET
                </Link>
                <Link
                  href="https://github.com/regix1/lancache-manager"
                  target="_blank"
                  className="text-sm text-default-500 hover:text-primary flex items-center gap-1"
                >
                  <Server className="h-3 w-3" />
                  GitHub
                </Link>
              </div>
            </div>
          </div>
        </footer>

        {/* Status Indicator */}
        <StatusIndicator />
      </div>
    </HeroUIProvider>
  );
}

export default App;