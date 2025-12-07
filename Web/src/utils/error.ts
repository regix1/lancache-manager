/**
 * Type guard to check if an error is an AbortError
 */
export function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

/**
 * Extract error message from an unknown error
 */
export function getErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}

/**
 * Extract error name from an unknown error
 */
export function getErrorName(error: unknown): string {
  if (error instanceof Error) {
    return error.name;
  }
  return '';
}
