/** Non-login fields must not be treated as saved or generated account credentials. */
export const noAutofill = {
  autoComplete: 'off',
  'data-bwignore': 'true',
  'data-1p-ignore': 'true'
} as const;
