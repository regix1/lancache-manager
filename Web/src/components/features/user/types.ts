export interface Session {
  id: string;
  deviceId?: string | null;
  deviceName: string | null;
  ipAddress: string | null;
  localIp: string | null;
  hostname: string | null;
  operatingSystem: string | null;
  browser: string | null;
  createdAt: string;
  lastSeenAt: string | null;
  expiresAt: string;
  isExpired: boolean;
  isRevoked: boolean;
  revokedAt?: string | null;
  revokedBy?: string | null;
  type: 'authenticated' | 'guest';
  // Prefill permissions (for guests only)
  prefillEnabled?: boolean;
  prefillExpiresAt?: string | null;
  isPrefillExpired?: boolean;
}

export interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  disableStickyNotifications: boolean;
  showDatasourceLabels: boolean;
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  showYearInDates: boolean;
  refreshRate?: string | null;
  allowedTimeFormats?: string[];
}

export interface ThemeOption {
  id: string;
  name: string;
}

export const refreshRateOptions = [
  { value: 'LIVE', label: 'Live (Real-time)' },
  { value: 'ULTRA', label: 'Ultra (1s)' },
  { value: 'REALTIME', label: 'Real-time (5s)' },
  { value: 'STANDARD', label: 'Standard (10s)' },
  { value: 'RELAXED', label: 'Relaxed (30s)' },
  { value: 'SLOW', label: 'Slow (60s)' }
];

export const durationOptions = [
  { value: '1', label: '1 hour' },
  { value: '2', label: '2 hours' },
  { value: '3', label: '3 hours' },
  { value: '6', label: '6 hours' },
  { value: '12', label: '12 hours' },
  { value: '24', label: '24 hours (1 day)' },
  { value: '48', label: '48 hours (2 days)' },
  { value: '72', label: '72 hours (3 days)' },
  { value: '168', label: '168 hours (1 week)' }
];

// Helper to clean IP addresses
export const cleanIpAddress = (ip: string): string => {
  const cleanIp = ip.replace('::ffff:', '');
  if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
    return 'localhost';
  }
  return cleanIp;
};

export const showToast = (type: 'success' | 'error' | 'info', message: string) => {
  window.dispatchEvent(
    new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    })
  );
};
