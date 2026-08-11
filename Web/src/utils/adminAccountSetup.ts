interface AdminAccountState {
  /** Whether first-run setup has already finished on this installation. */
  setupCompleted: boolean;
  /** The Security:EnableAuthentication flag as reported by the auth status route. */
  authenticationEnabled: boolean;
  /** Whether any account exists. Null when the server could not read the account table. */
  accountExists: boolean | null;
}

/**
 * True when an installation that already finished setup has to be sent back into the wizard to
 * create its first account.
 *
 * Installations that ran before accounts existed have no account row and every session they had was
 * revoked on the upgrade, so the sign-in screen has nothing to sign in with and no way to reach
 * account creation. The answer is never derived from the setup-completion flag alone: an
 * installation running with authentication switched off is legitimately account-less and must be
 * left where it is, but the day its operator switches authentication on it needs account creation
 * for the same reason. An unknown account state is treated as "not now", which keeps a server that
 * cannot read its database on the behaviour it has today. [37b]
 */
export function isAdminAccountRequired(state: AdminAccountState): boolean {
  return state.setupCompleted && state.authenticationEnabled && state.accountExists === false;
}
