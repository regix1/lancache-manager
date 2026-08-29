import React, { useCallback, useEffect, useState } from 'react';
import { NotificationsProvider } from '@contexts/notifications';
import { BulkRemovalProvider } from '@contexts/BulkRemovalContext';
import { CacheSizeProvider } from '@contexts/CacheSizeContext';
import { DashboardDataProvider } from '@contexts/DashboardDataContext';
import { TimeFilterProvider } from '@contexts/TimeFilterContext';
import { EventProvider } from '@contexts/EventContext';
import { CalendarSettingsProvider } from '@contexts/CalendarSettingsContext';
import { ClientGroupProvider } from '@contexts/ClientGroupContext';
import { ClientHostnameProvider } from '@contexts/ClientHostnameContext';
import { DownloadAssociationsProvider } from '@contexts/DownloadAssociationsContext';
import { RefreshRateProvider } from '@contexts/RefreshRateContext';
import { SignalRProvider } from '@contexts/SignalRContext';
import { ActivityProvider } from '@contexts/ActivityContext/ActivityProvider';
import { ConfigProvider } from '@contexts/ConfigContext';
import { SpeedProvider } from '@contexts/SpeedContext';
import { MockModeProvider } from '@contexts/MockModeContext';
import { useMockMode } from '@contexts/useMockMode';
import { GuestConfigProvider } from '@contexts/GuestConfigContext';
import { PicsProgressProvider } from '@contexts/PicsProgressContext';
import { SetupStatusProvider } from '@contexts/SetupStatusContext';
import { SteamAuthProvider } from '@contexts/SteamAuthContext';
import { PrefillProvider } from '@contexts/PrefillContext';
import { AuthProvider } from '@contexts/AuthContext';
import { UserPresenceProvider } from '@contexts/UserPresenceContext/UserPresenceProvider';
import { SteamWebApiStatusProvider } from '@contexts/SteamWebApiStatusContext';
import { TimezoneProvider } from '@contexts/TimezoneContext';
import { SessionPreferencesProvider } from '@contexts/SessionPreferencesContext';
import { DockerSocketProvider } from '@contexts/DockerSocketContext';
import { GameServiceProvider } from '@contexts/GameServiceContext';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import ErrorBoundary from '@components/common/ErrorBoundary';
import { ImageCacheContext, ImageInvalidateContext } from '@components/common/ImageCacheContext';
import ApiService from '@services/api.service';

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

const EventProviderWithMockMode: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { mockMode } = useMockMode();
  return <EventProvider mockMode={mockMode}>{children}</EventProvider>;
};

// The image cache version spans the whole app: the Downloads page renders banners and the
// Management page's game detection bumps the version, and each used to own its own provider with a
// value the other could not compare against. One provider here means GameImagesUpdated is still
// handled while the Downloads tab is unmounted, and every banner reads the same number.
const ImageCacheProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { on, off, isConnected } = useSignalR();
  const [imageCacheVersion, setImageCacheVersion] = useState(0);
  const invalidateImageCache = useCallback(() => {
    setImageCacheVersion((prev) => prev + 1);
  }, []);

  // Seed from the backend generation so the version survives a page reload.
  useEffect(() => {
    ApiService.getImageCacheVersion()
      .then((v) => {
        if (v > 0) setImageCacheVersion(v);
      })
      .catch((err: unknown) => {
        console.error('Failed to read the image cache version, banner URLs stay unversioned:', err);
      });
  }, []);

  useEffect(() => {
    const handleGameImagesUpdated = () => {
      setImageCacheVersion((prev) => prev + 1);
    };
    on('GameImagesUpdated', handleGameImagesUpdated);
    return () => {
      off('GameImagesUpdated', handleGameImagesUpdated);
    };
  }, [on, off]);

  // GameImagesUpdated events emitted while the connection was down are gone forever, so every
  // reconnect re-reads the backend generation and bumps past it to force a fresh /available fetch.
  const resyncImageCacheVersion = useCallback(() => {
    ApiService.getImageCacheVersion()
      .then((v) => {
        setImageCacheVersion((prev) => Math.max(prev, v));
      })
      .catch(() => {
        setImageCacheVersion((prev) => prev + 1);
      });
  }, []);
  useReconnectRefetch(isConnected, resyncImageCacheVersion);

  return (
    <ImageCacheContext.Provider value={imageCacheVersion}>
      <ImageInvalidateContext.Provider value={invalidateImageCache}>
        {children}
      </ImageInvalidateContext.Provider>
    </ImageCacheContext.Provider>
  );
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
              {/* Activity/presence subscription mounts here, above ConfigProvider's blocking gate, so it
                  subscribes to ActivityUpdated before the hub's connect-time seed snapshot arrives. */}
              <ActivityProvider>
                {/* Config layer - blocks rendering until config loads */}
                <ConfigProvider>
                  {/* Auth layer */}
                  <AuthProvider>
                    {/* Presence heartbeat mounts here, above every route, so LastSeenAtUtc keeps
                        being refreshed no matter which page is open - not just the Users page. */}
                    <UserPresenceProvider>
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
                                              <BulkRemovalProvider>
                                                <CacheSizeProvider>
                                                  <DashboardDataProviderWithMockMode>
                                                    {/* UI / calendar / event providers */}
                                                    <CalendarSettingsProvider>
                                                      <EventProviderWithMockMode>
                                                        <ClientGroupProvider>
                                                          <ClientHostnameProvider>
                                                            <DownloadAssociationsProvider>
                                                              <ImageCacheProvider>
                                                                {children}
                                                              </ImageCacheProvider>
                                                            </DownloadAssociationsProvider>
                                                          </ClientHostnameProvider>
                                                        </ClientGroupProvider>
                                                      </EventProviderWithMockMode>
                                                    </CalendarSettingsProvider>
                                                  </DashboardDataProviderWithMockMode>
                                                </CacheSizeProvider>
                                              </BulkRemovalProvider>
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
                    </UserPresenceProvider>
                  </AuthProvider>
                </ConfigProvider>
              </ActivityProvider>
            </SignalRProvider>
          </TimeFilterProvider>
        </GameServiceProvider>
      </MockModeProvider>
    </ErrorBoundary>
  );
};

export default AppProviders;
