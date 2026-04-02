import React from 'react';
import { NotificationsProvider } from '@contexts/notifications';
import { CacheSizeProvider } from '@contexts/CacheSizeContext';
import { DashboardDataProvider } from '@contexts/DashboardDataContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import { EventProvider } from '@contexts/EventContext';
import { CalendarSettingsProvider } from '@contexts/CalendarSettingsContext';
import { ClientGroupProvider } from '@contexts/ClientGroupContext';
import { DownloadAssociationsProvider } from '@contexts/DownloadAssociationsContext';
import { RefreshRateProvider } from '@contexts/RefreshRateContext';
import { SignalRProvider } from '@contexts/SignalRContext';
import { SpeedProvider } from '@contexts/SpeedContext';
import { MockModeProvider } from '@contexts/MockModeContext';
import { useMockMode } from '@contexts/useMockMode';
import { GuestConfigProvider } from '@contexts/GuestConfigContext';
import { PicsProgressProvider } from '@contexts/PicsProgressContext';
import { SetupStatusProvider } from '@contexts/SetupStatusContext';
import { SteamAuthProvider } from '@contexts/SteamAuthContext';
import { PrefillProvider } from '@contexts/PrefillContext';
import { AuthProvider } from '@contexts/AuthContext';
import { SteamWebApiStatusProvider } from '@contexts/SteamWebApiStatusContext';
import { TimezoneProvider } from '@contexts/TimezoneContext';
import { SessionPreferencesProvider } from '@contexts/SessionPreferencesContext';
import { DockerSocketProvider } from '@contexts/DockerSocketContext';
import { GameServiceProvider } from '@contexts/GameServiceContext';
import ErrorBoundary from '@components/common/ErrorBoundary';

// Wrapper components that inject mockMode from context into providers that require it.
// These must live here (inside MockModeProvider) so the useMockMode hook is available.
const DashboardDataProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({
  children
}) => {
  const { mockMode } = useMockMode();
  return <DashboardDataProvider mockMode={mockMode}>{children}</DashboardDataProvider>;
};

const PicsProgressProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({
  children
}) => {
  const { mockMode } = useMockMode();
  return <PicsProgressProvider mockMode={mockMode}>{children}</PicsProgressProvider>;
};

const AppProviders: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  return (
    <ErrorBoundary>
      {/* System / mock layer */}
      <MockModeProvider>
        <GameServiceProvider>
          {/* Data / filter layer */}
          <TimeFilterProvider>
            {/* Real-time communication */}
            <SignalRProvider>
              {/* Auth layer */}
              <AuthProvider>
                <DockerSocketProvider>
                  {/* User preferences */}
                  <SessionPreferencesProvider>
                    <RefreshRateProvider>
                      <SpeedProvider>
                        <TimezoneProvider>
                          {/* Steam / setup status */}
                          <SteamWebApiStatusProvider>
                            <GuestConfigProvider>
                              <SetupStatusProvider>
                                <SteamAuthProvider>
                                  <PrefillProvider>
                                    {/* Data providers */}
                                    <PicsProgressProviderWithMockMode>
                                      <NotificationsProvider>
                                        <CacheSizeProvider>
                                          <DashboardDataProviderWithMockMode>
                                            {/* UI / calendar / event providers */}
                                            <CalendarSettingsProvider>
                                              <EventProvider>
                                                <ClientGroupProvider>
                                                  <DownloadAssociationsProvider>
                                                    {children}
                                                  </DownloadAssociationsProvider>
                                                </ClientGroupProvider>
                                              </EventProvider>
                                            </CalendarSettingsProvider>
                                          </DashboardDataProviderWithMockMode>
                                        </CacheSizeProvider>
                                      </NotificationsProvider>
                                    </PicsProgressProviderWithMockMode>
                                  </PrefillProvider>
                                </SteamAuthProvider>
                              </SetupStatusProvider>
                            </GuestConfigProvider>
                          </SteamWebApiStatusProvider>
                        </TimezoneProvider>
                      </SpeedProvider>
                    </RefreshRateProvider>
                  </SessionPreferencesProvider>
                </DockerSocketProvider>
              </AuthProvider>
            </SignalRProvider>
          </TimeFilterProvider>
        </GameServiceProvider>
      </MockModeProvider>
    </ErrorBoundary>
  );
};

export default AppProviders;
