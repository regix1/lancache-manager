export type AccountMode = 'password' | 'apiKeyPassword' | 'apiKeyOidc' | 'oidc' | 'unauthenticated';

export function requiresApiKey(mode: AccountMode): boolean {
  return mode === 'apiKeyPassword' || mode === 'apiKeyOidc';
}

export function usesOidc(mode: AccountMode): boolean {
  return mode === 'apiKeyOidc' || mode === 'oidc';
}
