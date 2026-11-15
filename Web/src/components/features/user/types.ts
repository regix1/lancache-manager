import type { TFunction } from 'i18next';
import type { SessionType } from '@services/auth.service';
import { APP_EVENTS } from '@utils/constants';

export interface Session {
  id: string;
  sessionType?: SessionType;
  username?: string | null;
  accountDeleted: boolean;
  ipAddress: string | null;
  userAgent: string | null;
  createdAt: string;
  lastSeenAt: string | null;
  expiresAt: string;
  isExpired: boolean;
  isRevoked: boolean;
  revokedAt?: string | null;
  isCurrentSession: boolean;
  /** True for the one session every browser runs on while sign-in is off; the server refuses to revoke or delete it then. Mirrors SessionRecord.IsSharedSession. */
  isSharedSession: boolean;
  prefillEnabled: boolean;
  prefillExpiresAt?: string | null;
  steamPrefillEnabled: boolean;
  steamPrefillExpiresAt?: string | null;
  epicPrefillEnabled: boolean;
  epicPrefillExpiresAt?: string | null;
  battlenetPrefillEnabled: boolean;
  battlenetPrefillExpiresAt?: string | null;
  riotPrefillEnabled: boolean;
  riotPrefillExpiresAt?: string | null;
  xboxPrefillEnabled: boolean;
  xboxPrefillExpiresAt?: string | null;
  publicIpAddress?: string | null;
  countryName?: string | null;
  regionName?: string | null;
  city?: string | null;
  timezone?: string | null;
  ispName?: string | null;
  screenResolution?: string | null;
  browserLanguage?: string | null;
  /** Account behind this session. Absent for guests and shared access: the server omits null fields (Program.cs:71). Mirrors SessionRecord.AccountId. */
  accountId?: string | null;
  /** True when the session ended because its user signed out. A signed-out session also has isRevoked true, so check signedOut before isRevoked. Mirrors SessionRecord.SignedOut. */
  signedOut: boolean;
}

// Wire-shape mirror of C# `GuestDurationResponse` in Models/Responses/AuthResponses.cs.
export interface GuestDurationResponse {
  durationHours: number;
  source: 'ui' | 'config' | 'default';
  canEdit: boolean;
  envVarValue: number;
}

/** Account actions the host owns; the panes call them. */
export interface AccountActions {
  onEdit: (account: UserAccount) => void;
  onSetDisabled: (account: UserAccount, disabled: boolean) => void;
  onDelete: (account: UserAccount) => void;
  /** Account whose disable or enable request is out, or null. */
  disablingAccountId: string | null;
}

export interface ThemeOption {
  id: string;
  name: string;
}

/** Read-only stored kind: Admin for the installation owner, User for an ordinary account. */
export type AccountRole = 'admin' | 'user';

/** One row of GET /api/accounts. Mirrors the server's AccountResponse. */
export interface UserAccount {
  id: string;
  username: string;
  role: AccountRole;
  /** The account that owns the installation. It cannot be edited, disabled or deleted. */
  isMainAdmin: boolean;
  isDisabled: boolean;
  createdAtUtc: string;
  /** Left out until the account signs in for the first time: the server omits null fields (Program.cs:71). */
  lastLoginAtUtc?: string | null;
}

/** The account waiting for deletion confirmation. */
export interface AccountConfirmation {
  account: UserAccount;
}

/** The open account form. `account` is null while it is creating one. */
export interface AccountEditor {
  account: UserAccount | null;
  username: string;
  password: string;
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
interface ParsedUserAgent {
  browser: string | null; // null when no known browser matched
  browserVersion: string;
  os: string | null; // null when no known system matched
  title: string; // e.g. "Chrome on Windows"
}

export const parseUserAgent = (ua: string | null): ParsedUserAgent => {
  if (!ua) return { browser: null, browserVersion: '', os: null, title: 'Unknown Device' };

  let browser: string | null = null;
  let browserVersion = '';
  let os: string | null = null;

  // Detect browser (order matters - check specific before generic). Every iOS browser sends "Safari/"
  // and names itself with its own iOS token (EdgiOS, FxiOS, CriOS) instead of Edg, Firefox or Chrome.
  if (ua.includes('Edg/') || ua.includes('EdgiOS/')) {
    browser = 'Edge';
    browserVersion = ua.match(/Edg(?:iOS)?\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('OPR/') || ua.includes('Opera')) {
    browser = 'Opera';
    browserVersion = ua.match(/(?:OPR|Opera)\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Vivaldi/')) {
    browser = 'Vivaldi';
    browserVersion = ua.match(/Vivaldi\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Brave')) {
    browser = 'Brave';
    browserVersion = ua.match(/Chrome\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Firefox/') || ua.includes('FxiOS/')) {
    browser = 'Firefox';
    browserVersion = ua.match(/(?:Firefox|FxiOS)\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Safari/') && !ua.includes('Chrome/') && !ua.includes('CriOS/')) {
    browser = 'Safari';
    browserVersion = ua.match(/Version\/([\d.]+)/)?.[1] ?? '';
  } else if (ua.includes('Chrome/') || ua.includes('CriOS/')) {
    browser = 'Chrome';
    browserVersion = ua.match(/(?:Chrome|CriOS)\/([\d.]+)/)?.[1] ?? '';
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
  } else if (ua.includes('iPhone') || ua.includes('iPad')) {
    // Before the Mac check: iPhone and iPad agents also contain "like Mac OS X".
    os = 'iOS';
  } else if (ua.includes('Mac OS X') || ua.includes('Macintosh')) {
    os = 'macOS';
  } else if (ua.includes('Android')) {
    const ver = ua.match(/Android ([\d.]+)/)?.[1];
    os = ver ? `Android ${ver.split('.')[0]}` : 'Android';
  } else if (ua.includes('Linux')) {
    os = 'Linux';
  } else if (ua.includes('CrOS')) {
    os = 'Chrome OS';
  }

  const title = [browser, os].filter((part): part is string => part !== null).join(' on ');

  return { browser, browserVersion, os, title };
};

// parseUserAgent keeps returning plain English: its strings are also the search haystack in
// sessionMatchesSearch, which matches against the raw user agent. An unknown browser or system
// comes back as null. Only the text that reaches the screen is translated, here, and that text
// joins the haystack so a search for the shown "Unknown" finds the row.
export const deviceTitle = (userAgent: string | null, t: TFunction): string => {
  if (!userAgent) return t('activeSessions.device.unknownDevice');
  const parsed = parseUserAgent(userAgent);
  const browser =
    parsed.browser === null ? t('activeSessions.device.unknownBrowser') : parsed.browser;
  return parsed.os !== null
    ? t('activeSessions.device.browserOnOs', { browser, os: parsed.os })
    : browser;
};

export const sessionName = (session: Session, t: TFunction): string => {
  const username = session.username?.trim();
  if (username) return username;
  if (session.accountDeleted) return t('activeSessions.deletedAccount');
  if (session.sessionType === 'guest') return t('activeSessions.filters.guest');
  return t('activeSessions.sharedAccess');
};

// Format "Country / Region / City" into a compact display string, skipping
// blanks and avoiding duplicates (some providers echo the region as the city).
export const formatLocation = (
  city: string | null | undefined,
  region: string | null | undefined,
  country: string | null | undefined
): string | null => {
  const parts = [city, region, country]
    .map((p) => (typeof p === 'string' ? p.trim() : ''))
    .filter((p): p is string => p.length > 0);
  const seen = new Set<string>();
  const unique = parts.filter((p) => {
    const key = p.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
  return unique.length > 0 ? unique.join(', ') : null;
};

// Helper to clean IP addresses
export const cleanIpAddress = (ip: string): string => {
  // Anchored so only the IPv4-mapped prefix is stripped. An unanchored replace would also cut
  // a matching group out of the middle of a real IPv6 address and render a different address.
  const cleanIp = ip.startsWith('::ffff:') ? ip.slice(7) : ip;
  if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
    return 'localhost';
  }
  return cleanIp;
};

export const showToast = (type: 'success' | 'error' | 'info', message: string) => {
  window.dispatchEvent(
    new CustomEvent(APP_EVENTS.SHOW_TOAST, {
      detail: { type, message }
    })
  );
};
