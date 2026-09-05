export const getPasswordStrength = (password: string): 'weak' | 'medium' | 'strong' => {
  const hasUppercase = /[A-Z]/.test(password);
  const hasLowercase = /[a-z]/.test(password);
  const hasNumbers = /[0-9]/.test(password);
  const hasSymbols = /[^A-Za-z0-9]/.test(password);
  const hasMixedCase = hasUppercase && hasLowercase;
  const hasNumbersOrSymbols = hasNumbers || hasSymbols;

  if (password.length >= 15 && hasMixedCase && hasNumbersOrSymbols) {
    return 'strong';
  }
  if (password.length >= 10 && (hasMixedCase || hasNumbers)) {
    return 'medium';
  }
  return 'weak';
};
