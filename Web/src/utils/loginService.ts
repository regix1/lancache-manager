import { usesOidc, type AccountMode } from './accountMode';

/** Wire values of `loginServices[].kind`, `pendingLoginKind` and `login.kind` on /api/auth. */
export type LoginKind = 'google' | 'github' | 'microsoft' | 'apple' | 'customOidc';

/** One tested connection as the public status advertises it. Never carries a secret. */
export interface LoginService {
  id: string;
  kind: LoginKind;
  displayName: string;
}

/** Order the setup screen offers the kinds in. Custom OpenID Connect stays last as "Other". */
export const LOGIN_KINDS: readonly LoginKind[] = [
  'google',
  'github',
  'microsoft',
  'apple',
  'customOidc'
];

/** Kinds whose registration console issues a client secret. Apple signs its own on the server. */
export function needsClientSecret(kind: LoginKind): boolean {
  return kind !== 'apple';
}

/**
 * The buttons an ordinary visitor sees. The status lists every tested connection, including ones
 * kept dormant while the mode is password or unauthenticated, and those must only ever be reached
 * through the owner reauthentication flow.
 */
export function signInServices(
  services: readonly LoginService[],
  mode: AccountMode
): LoginService[] {
  return usesOidc(mode) ? [...services] : [];
}

/** Both exact redirect paths the identity service has to allow for one kind. */
export function callbackPaths(kind: LoginKind): { callback: string; setupCallback: string } {
  const base = kind === 'customOidc' ? '/api/auth/oidc' : `/api/auth/login/${kind}`;
  return { callback: `${base}/callback`, setupCallback: `${base}/setup-callback` };
}

/**
 * The failure categories a sign-in callback can send back in `?oidcError=`. Anything else, and any
 * text the identity service produced, is never shown; it falls through to the general message.
 */
const LOGIN_ERROR_CODES: readonly string[] = [
  'connection',
  'authentication',
  'identity',
  'expired',
  'state',
  'unavailable'
];

/** The translation key for one bounded failure code, or the general failure message. */
export function loginErrorKey(code: string | null): string {
  return code !== null && LOGIN_ERROR_CODES.includes(code)
    ? `accessSetup.errors.${code}`
    : 'accessSetup.oidcFailed';
}
