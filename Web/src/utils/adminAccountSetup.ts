interface AdminAccountState {
  /** The Security:EnableAuthentication flag as reported by the auth status route. */
  authenticationEnabled: boolean;
  /** Whether any account exists. Null when the server could not read the account table. */
  accountExists: boolean | null;
  /** Whether the database connection details still have to be supplied through the wizard. */
  needsPostgresCredentials: boolean;
  /**
   * Whether the post-start window is open for resetting the main administrator password.
   * The same wizard account step collects the username and password; only the submit target
   * changes.
   */
  mainAdminRecoveryAvailable: boolean;
}

/**
 * True when an installation has to be sent into the wizard before it can be signed in to.
 *
 * A brand-new installation and one that has been running since before accounts existed both arrive
 * here with no account row, and neither of them has anything to sign in with, so both need the
 * wizard's account step. Whether setup has finished is deliberately not part of the answer: reading
 * it would send a fresh installation to a sign-in form it cannot fill in. An installation running
 * with authentication switched off is legitimately account-less and must be left where it is, but
 * the day its operator switches authentication on it needs account creation for the same reason.
 *
 * An unknown account state usually means "not now", because the sign-in screen is where an
 * installation whose database went away belongs: it kept its credentials, so the answer can still
 * arrive. The exception is an installation whose credentials have not been supplied yet, which is
 * the one state where signing in can never resolve the unknown, because there is no database to
 * hold the account and no screen but the wizard that can point one out.
 *
 * A completed installation that just reopened the recovery window is the other case that belongs
 * on this step: the account exists, but the operator has no password to sign in with until they
 * set a new one on the same form.
 */
export function isAdminAccountRequired(state: AdminAccountState): boolean {
  return (
    state.authenticationEnabled &&
    (state.accountExists === false ||
      (state.accountExists === null && state.needsPostgresCredentials) ||
      state.mainAdminRecoveryAvailable)
  );
}
