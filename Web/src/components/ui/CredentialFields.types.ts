/** Which of the three credential inputs a change came from. */
export type CredentialField = 'apiKey' | 'username' | 'password';

export interface CredentialFieldsProps {
  apiKey: string;
  username: string;
  password: string;
  onChange: (field: CredentialField, value: string) => void;
  /** Runs when Enter is pressed with all three fields filled. */
  onSubmit: () => void;
  /** Blocks all three inputs while a sign-in or a database reset is in flight. */
  disabled: boolean;
  /**
   * The API key hint is the one thing that differs per screen: a format example on the settings
   * screens, and wording that changes during a database reset in the sign-in modal.
   */
  apiKeyPlaceholder: string;
  /** Puts the cursor in the API key field on mount. For a screen that opens onto this form. */
  autoFocus?: boolean;
}
