import i18n from '@/i18n';
import { assertOk, type ApiErrorData } from './apiError';
import { isAbortError } from '@utils/error';
import { antiforgeryHeaders } from '@utils/antiforgery';
import { APP_EVENTS, getApiUrl } from '@utils/constants';
import { hasRecentUserInteraction } from '@utils/userInteractionTracker';
import type { AccountMode } from '@utils/accountMode';
import type { LoginKind, LoginService } from '@utils/loginService';
export type { AccountMode } from '@utils/accountMode';

export type AuthMode = 'authenticated' | 'guest' | 'unauthenticated';

interface AccessSetupRequest {
  mode: AccountMode;
  apiKey: string;
  acknowledgeUnauthenticated: boolean;
  /**
   * One named connection to stage for testing. Absent when the request only changes the mode or
   * keeps connections that were tested before. Only the fields the kind needs are sent.
   */
  login?: {
    kind: LoginKind;
    clientId: string;
    clientSecret?: string;
    displayName?: string;
    tenant?: string;
    authority?: string;
    allowedSubjects?: string[];
    teamId?: string;
    keyId?: string;
    privateKey?: string;
  };
  /** Legacy custom OpenID Connect body; new callers send `login` with kind `customOidc`. */
  oidc?: {
    authority: string;
    clientId: string;
    clientSecret: string;
    displayName: string;
    allowedSubjects: string[];
  };
}

interface AccessSetupResponse {
  success: boolean;
  /** Compatibility alias of requiresLoginTest. */
  requiresOidcTest: boolean;
  requiresLoginTest: boolean;
  pendingLoginId: string | null;
  callbackUrls: string[];
}
export type SessionType = 'admin' | 'user' | 'guest';

/**
 * Admin and user sessions both sign in as an account and have the same access; a guest has neither.
 * Mirrors SessionTypeExtensions.IsAccountHolder on the server.
 */
export function isAccountHolder(sessionType: SessionType | null): boolean {
  return sessionType === 'admin' || sessionType === 'user';
}

interface AuthStatusResponse {
  isAuthenticated: boolean;
  authenticationEnabled: boolean;
  accountMode: AccountMode;
  authenticationSetupRequired: boolean;
  oidcDisplayName: string;
  oidcPending: boolean;
  ownerOidcEnabled: boolean;
  /** False for a main administrator created by external sign-in who never set a local password. */
  ownerPasswordEnabled: boolean;
  /** Every tested connection, dormant ones included. Filter with signInServices() before showing. */
  loginServices: LoginService[];
  /** Ids from loginServices the stored owner may reauthenticate through. */
  ownerLoginServices: string[];
  loginSetupPending: boolean;
  pendingLoginKind: LoginKind | null;
  sessionType: SessionType | null;
  sessionId: string | null;
  expiresAt: string | null;
  accountId: string | null;
  isMainAdmin: boolean;
  hasData: boolean;
  hasBeenInitialized: boolean;
  hasDataLoaded: boolean;
  guestAccessEnabled: boolean;
  guestDurationHours: number;
  prefillEnabled: boolean;
  prefillExpiresAt: string | null;
  steamPrefillEnabled: boolean;
  steamPrefillExpiresAt: string | null;
  epicPrefillEnabled: boolean;
  epicPrefillExpiresAt: string | null;
  battlenetPrefillEnabled: boolean;
  battlenetPrefillExpiresAt: string | null;
  riotPrefillEnabled: boolean;
  riotPrefillExpiresAt: string | null;
  xboxPrefillEnabled: boolean;
  xboxPrefillExpiresAt: string | null;
  /**
   * False only when the status route was not reached at all, so a caller can tell "unknown" from
   * "empty". A status the server answered with an error still counts as reached.
   */
  reachable?: boolean;
}

interface LoginResponse {
  success: boolean;
  sessionType: SessionType;
  expiresAt: string;
  error?: string;
}

const API_URL = getApiUrl();
const AUTH_CHECK_TIMEOUT_MS = 10000;

class AuthService {
  private authQueue: Promise<void> = Promise.resolve();

  public isAuthenticated = false;
  public authenticationSetupRequired = true;
  public authChecked = false;
  public authMode: AuthMode = 'unauthenticated';
  public sessionType: SessionType | null = null;
  public sessionId: string | null = null;
  // The sign-in screen shows the guest duration and offers the guest button to a visitor who has no
  // session at all, and the guest config routes all require one, so both settings are kept from the
  // status call instead. Until the first call answers they hold the same values the server ships.
  public guestAccessEnabled = true;
  public guestDurationHours = 6;

  /**
   * Session status responses set cookies as well as local state. Keep every status read and session
   * change in request order so an older guest response cannot arrive after an account sign-in and
   * replace the account cookie.
   */
  private serialize<T>(work: () => Promise<T>): Promise<T> {
    const result = this.authQueue.then(work, work);
    this.authQueue = result.then(
      () => undefined,
      () => undefined
    );
    return result;
  }

  checkAuth(): Promise<AuthStatusResponse> {
    return this.serialize(() => this.fetchAuthStatus());
  }

  private async fetchAuthStatus(): Promise<AuthStatusResponse> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => {
      controller.abort();
    }, AUTH_CHECK_TIMEOUT_MS);

    try {
      // Reachable from a SignalR-reconnect effect with no user action involved (a network blip or
      // laptop wake can reconnect while the tab is genuinely idle) - carry the same activity signal
      // as ApiService.getFetchOptions() so this doesn't keep LastSeenAtUtc artificially fresh.
      const response = await fetch(`${API_URL}/api/auth/status`, {
        credentials: 'include',
        signal: controller.signal,
        headers: {
          'X-User-Active': hasRecentUserInteraction(120_000) ? 'true' : 'false'
        }
      });

      await assertOk(response);

      const data: AuthStatusResponse = await response.json();

      this.isAuthenticated = data.isAuthenticated;
      this.authenticationSetupRequired = data.authenticationSetupRequired === true;
      this.sessionType = data.sessionType;
      this.sessionId = data.sessionId;
      this.authChecked = true;
      this.guestAccessEnabled = data.guestAccessEnabled;
      this.guestDurationHours = data.guestDurationHours;

      if (data.isAuthenticated && isAccountHolder(data.sessionType)) {
        this.authMode = 'authenticated';
      } else if (data.isAuthenticated && data.sessionType === 'guest') {
        this.authMode = 'guest';
      } else {
        this.authMode = 'unauthenticated';
      }

      return { ...data, reachable: true };
    } catch (error: unknown) {
      const aborted = isAbortError(error);
      if (aborted) {
        console.warn(`[AuthService] checkAuth timed out after ${AUTH_CHECK_TIMEOUT_MS}ms`);
      }
      console.error('[AuthService] checkAuth error:', error);
      // Only a call that never got an answer keeps the last-known session: the 10s abort above, or
      // a dropped network, which rejects fetch with a TypeError. AuthContext already keeps its own
      // state on that answer, and these flags gate the SignalR connection - zeroing them here made
      // the AUTH_SESSION_UPDATED dispatched in its finally stop a live socket over a blip, with
      // nothing left to restart it.
      const reachable = !aborted && !(error instanceof TypeError);
      if (reachable) {
        this.isAuthenticated = false;
        this.authMode = 'unauthenticated';
        this.sessionType = null;
        this.sessionId = null;
      }
      this.authChecked = true;

      return {
        isAuthenticated: false,
        authenticationEnabled: true,
        accountMode: 'apiKeyPassword',
        authenticationSetupRequired: false,
        oidcDisplayName: '',
        oidcPending: false,
        ownerOidcEnabled: false,
        ownerPasswordEnabled: false,
        loginServices: [],
        ownerLoginServices: [],
        loginSetupPending: false,
        pendingLoginKind: null,
        sessionType: null,
        sessionId: null,
        expiresAt: null,
        accountId: null,
        isMainAdmin: false,
        hasData: false,
        hasBeenInitialized: false,
        hasDataLoaded: false,
        guestAccessEnabled: true,
        guestDurationHours: 6,
        prefillEnabled: false,
        prefillExpiresAt: null,
        steamPrefillEnabled: false,
        steamPrefillExpiresAt: null,
        epicPrefillEnabled: false,
        epicPrefillExpiresAt: null,
        battlenetPrefillEnabled: false,
        battlenetPrefillExpiresAt: null,
        riotPrefillEnabled: false,
        riotPrefillExpiresAt: null,
        xboxPrefillEnabled: false,
        xboxPrefillExpiresAt: null,
        reachable
      };
    } finally {
      clearTimeout(timeoutId);
    }
  }

  login(
    apiKey: string,
    username: string,
    password: string
  ): Promise<{ success: boolean; message?: string }> {
    return this.serialize(async () => {
      try {
        // The antiforgery token the server issues belongs to whoever asked for it, so one left over
        // from a session that has since ended - signed out, revoked by an admin, expired - is refused
        // here rather than at the call the user is actually making. Reading the status first is what
        // makes the token match the caller the server is about to see, and it is the same call the
        // page already makes on load, so this only does real work when the caller has changed.
        await this.fetchAuthStatus();

        const response = await fetch(`${API_URL}/api/auth/login`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', ...antiforgeryHeaders() },
          credentials: 'include',
          body: JSON.stringify({ apiKey, username, password })
        });

        if (!response.ok) {
          // Deliberate exception to the "throw ApiError" rule (documented in the error-handling
          // standard): this method's contract is to RETURN {success,message} so the caller can render
          // a form error, not to throw. It still parses the shared ApiErrorData shape.
          const data: ApiErrorData = await response.json().catch(() => ({}));
          return {
            success: false,
            message:
              data.error ||
              data.message ||
              i18n.t('auth.errors.loginFailedStatus', { status: response.status })
          };
        }

        const data: LoginResponse = await response.json();
        if (data.success) {
          this.isAuthenticated = true;
          this.authMode = 'authenticated';
          // The role comes from the account that signed in, so a user session is not recorded as admin.
          this.sessionType = data.sessionType;
        }

        return { success: data.success, message: data.error };
      } catch (error: unknown) {
        console.error('[AuthService] login error:', error);
        return { success: false, message: i18n.t('auth.errors.networkDuringLogin') };
      }
    });
  }

  configureAccess(request: AccessSetupRequest): Promise<AccessSetupResponse> {
    return this.serialize(async () => {
      await this.fetchAuthStatus();
      const response = await fetch(`${API_URL}/api/auth/setup`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json', ...antiforgeryHeaders() },
        body: JSON.stringify(request)
      });
      await assertOk(response);
      return response.json();
    });
  }

  startOidc(apiKey: string, setup = false, owner = false): Promise<{ url: string }> {
    return this.serialize(async () => {
      await this.fetchAuthStatus();
      const response = await fetch(`${API_URL}/api/auth/oidc/start`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json', ...antiforgeryHeaders() },
        body: JSON.stringify({ apiKey, setup, owner })
      });
      await assertOk(response);
      return response.json();
    });
  }

  /**
   * Fetches the single-use challenge URL for one named connection; the caller navigates the whole
   * page to it. `setup` picks the one pending connection, `owner` reauthenticates the stored owner
   * through an active or dormant connection, and the key travels only for the flows that need it.
   */
  startLogin(
    loginId: string,
    apiKey: string,
    setup = false,
    owner = false
  ): Promise<{ url: string }> {
    return this.serialize(async () => {
      await this.fetchAuthStatus();
      const response = await fetch(`${API_URL}/api/auth/login/start`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json', ...antiforgeryHeaders() },
        body: JSON.stringify({ loginId, apiKey, setup, owner })
      });
      await assertOk(response);
      return response.json();
    });
  }

  /**
   * Gives a main administrator created by external sign-in a first local password. The server
   * requires the current main-admin session, the installation key and the usual credential rules,
   * and refuses once a password exists; that case belongs to the account edit screen.
   */
  setMainAdminPassword(apiKey: string, username: string, password: string): Promise<void> {
    return this.serialize(async () => {
      await this.fetchAuthStatus();
      const response = await fetch(`${API_URL}/api/account-setup/main-admin-password`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json', ...antiforgeryHeaders() },
        body: JSON.stringify({ apiKey, username, password })
      });
      await assertOk(response);
    });
  }

  /** Drops one tested connection. The server refuses the last active one while SSO is selected. */
  removeLoginService(id: string, apiKey: string): Promise<void> {
    return this.serialize(async () => {
      await this.fetchAuthStatus();
      const response = await fetch(`${API_URL}/api/auth/login-services/${encodeURIComponent(id)}`, {
        method: 'DELETE',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json', ...antiforgeryHeaders() },
        body: JSON.stringify({ apiKey })
      });
      await assertOk(response);
    });
  }

  startGuestSession(): Promise<{ success: boolean; message?: string }> {
    return this.serialize(async () => {
      try {
        // Same reason as login above: a guest whose session was cleared still holds that session's
        // antiforgery token, and starting another one is a POST that would be refused for it.
        await this.fetchAuthStatus();

        const response = await fetch(`${API_URL}/api/auth/guest`, {
          method: 'POST',
          headers: antiforgeryHeaders(),
          credentials: 'include'
        });

        if (!response.ok) {
          // Same deliberate {success,message} return contract as login() above.
          const data: ApiErrorData = await response.json().catch(() => ({}));
          return {
            success: false,
            message: data.error || data.message || i18n.t('auth.errors.guestSessionFailed')
          };
        }

        const data: LoginResponse = await response.json();
        if (data.success) {
          this.isAuthenticated = true;
          this.authMode = 'guest';
          this.sessionType = 'guest';
        }

        return { success: data.success, message: data.error };
      } catch (error: unknown) {
        console.error('[AuthService] startGuestSession error:', error);
        return { success: false, message: i18n.t('auth.errors.network') };
      }
    });
  }

  logout(): Promise<void> {
    return this.serialize(async () => {
      try {
        await fetch(`${API_URL}/api/auth/logout`, {
          method: 'POST',
          headers: antiforgeryHeaders(),
          credentials: 'include'
        });
      } catch (error: unknown) {
        // The finally below signs this device out regardless, so the UI says "logged out" while the
        // session the server holds may still be live. Only this tells the user to try again.
        console.error('[AuthService] logout error:', error);
        window.dispatchEvent(
          new CustomEvent(APP_EVENTS.SHOW_TOAST, {
            detail: { type: 'error', message: i18n.t('auth.errors.logoutIncomplete') }
          })
        );
      } finally {
        this.isAuthenticated = false;
        this.authMode = 'unauthenticated';
        this.sessionType = null;
        this.sessionId = null;
      }
    });
  }

  isAdmin(): boolean {
    return this.sessionType === 'admin';
  }

  isGuest(): boolean {
    return this.sessionType === 'guest';
  }

  getSessionType(): SessionType | null {
    return this.sessionType;
  }

  isGuestModeActive(): boolean {
    return this.authMode === 'guest';
  }
}

const authService = new AuthService();
export default authService;
