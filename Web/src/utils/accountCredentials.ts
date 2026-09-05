/** The first rule each account credential breaks, as a translation key, or null when it passes. */
export interface AccountCredentialErrors {
  username: string | null;
  password: string | null;
  confirmPassword: string | null;
}

/**
 * Same rules and same order as AccountCredentialsRequestValidator, so the user is told before
 * submitting rather than by a 400. The server stays the authority. Shared by the first-admin step
 * and the local-password panel an externally created owner fills in before choosing a password
 * mode, so both screens refuse the same passwords.
 */
export function validateAccountCredentials(
  rawUsername: string,
  password: string,
  confirmPassword: string
): AccountCredentialErrors {
  const username = rawUsername.trim();
  const errors: AccountCredentialErrors = { username: null, password: null, confirmPassword: null };

  if (!username) {
    errors.username = 'initialization.adminAccount.errors.usernameRequired';
  } else if (username.length > 64) {
    errors.username = 'initialization.adminAccount.errors.usernameTooLong';
  }

  if (!password) {
    errors.password = 'initialization.adminAccount.errors.passwordRequired';
  } else if (password.length < 12) {
    errors.password = 'initialization.adminAccount.errors.passwordTooShort';
  } else if (password.length > 256) {
    errors.password = 'initialization.adminAccount.errors.passwordTooLong';
  } else {
    // char.IsLower and its siblings are Unicode-aware on the server, so ASCII-only classes here
    // would reject a password the server accepts and leave the operator unable to create the one
    // account there is.
    const characterClasses =
      (/\p{Ll}/u.test(password) ? 1 : 0) +
      (/\p{Lu}/u.test(password) ? 1 : 0) +
      (/\p{Nd}/u.test(password) ? 1 : 0) +
      (/[^\p{L}\p{Nd}]/u.test(password) ? 1 : 0);

    if (characterClasses < 3) {
      errors.password = 'initialization.adminAccount.errors.passwordCharacterClasses';
    } else if (password.toLowerCase() === username.toLowerCase()) {
      errors.password = 'initialization.adminAccount.errors.passwordSameAsUsername';
    }
  }

  if (!confirmPassword) {
    errors.confirmPassword = 'initialization.adminAccount.errors.confirmPasswordRequired';
  } else if (password !== confirmPassword) {
    errors.confirmPassword = 'initialization.adminAccount.errors.passwordsDoNotMatch';
  }

  return errors;
}
