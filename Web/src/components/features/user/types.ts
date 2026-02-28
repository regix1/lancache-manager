export interface Session {
  id: string;
  sessionType?: 'admin' | 'guest';
  ipAddress: string | null;
  userAgent: string | null;
  createdAt: string;
  lastSeenAt: string | null;
  expiresAt: string;
  isExpired: boolean;
  isRevoked: boolean;
  revokedAt?: string | null;
  isCurrentSession: boolean;
  prefillEnabled: boolean;
  prefillExpiresAt?: string | null;
  steamPrefillEnabled: boolean;
  steamPrefillExpiresAt?: string | null;
  epicPrefillEnabled: boolean;
  epicPrefillExpiresAt?: string | null;
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
  refreshRateLocked?: boolean | null;
  allowedTimeFormats?: string[];
  maxThreadCount?: number | null;
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

// Parse UserAgent into friendly browser/OS info
export interface ParsedUserAgent {
  browser: string;
  browserVersion: string;
  os: string;
  title: string; // e.g. "Chrome on Windows"
}

export const parseUserAgent = (ua: string | null): ParsedUserAgent => {
  if (!ua) return { browser: 'Unknown', browserVersion: '', os: 'Unknown', title: 'Unknown Device' };

  let browser = 'Unknown';
  let browserVersion = '';
  let os = 'Unknown';

  // Detect browser (order matters — check specific before generic)
  if (ua.includes('Edg/')) {
    browser = 'Edge';
    browserVersion = ua.match(/Edg\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('OPR/') || ua.includes('Opera')) {
    browser = 'Opera';
    browserVersion = ua.match(/(?:OPR|Opera)\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Vivaldi/')) {
    browser = 'Vivaldi';
    browserVersion = ua.match(/Vivaldi\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Brave')) {
    browser = 'Brave';
    browserVersion = ua.match(/Chrome\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Firefox/')) {
    browser = 'Firefox';
    browserVersion = ua.match(/Firefox\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Safari/') && !ua.includes('Chrome/')) {
    browser = 'Safari';
    browserVersion = ua.match(/Version\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Chrome/')) {
    browser = 'Chrome';
    browserVersion = ua.match(/Chrome\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('curl/')) {
    browser = 'curl';
    browserVersion = ua.match(/curl\/([\d.]+)/)?.[1] ?? '';
  }

  // Shorten version to major.minor
  if (browserVersion) {
    const parts = browserVersion.split('.');
    browserVersion = parts.length >= 2 ? `${parts[0]}.${parts[1]}` : parts[0];
  }

  // Detect OS
  if (ua.includes('Windows NT 10.0')) {
    os = 'Windows';
  } else if (ua.includes('Windows NT')) {
    os = 'Windows';
  } else if (ua.includes('Mac OS X') || ua.includes('Macintosh')) {
    os = 'macOS';
  } else if (ua.includes('Android')) {
    const ver = ua.match(/Android ([\d.]+)/)?.[1];
    os = ver ? `Android ${ver.split('.')[0]}` : 'Android';
  } else if (ua.includes('iPhone') || ua.includes('iPad')) {
    os = 'iOS';
  } else if (ua.includes('Linux')) {
    os = 'Linux';
  } else if (ua.includes('CrOS')) {
    os = 'Chrome OS';
  }

  const title = os !== 'Unknown' ? `${browser} on ${os}` : browser;

  return { browser, browserVersion, os, title };
};

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
