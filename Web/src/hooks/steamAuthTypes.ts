export interface SteamLoginFlowState {
  loading: boolean;
  needsTwoFactor: boolean;
  needsEmailCode: boolean;
  waitingForMobileConfirmation: boolean;
  useManualCode: boolean;
  username: string;
  password: string;
  twoFactorCode: string;
  emailCode: string;
  /** Epic OAuth: whether we're waiting for the user to provide an authorization code */
  needsAuthorizationCode: boolean;
  /** Epic OAuth: the URL the user must visit to authorize */
  authorizationUrl: string;
  /** Epic OAuth: the authorization code entered by the user */
  authorizationCode: string;
  /** Xbox device-code: whether we're waiting for the user to approve via the device flow */
  needsDeviceCode: boolean;
  /** Xbox device-code: the short user code to enter at the verification URL */
  deviceUserCode: string;
  /** Xbox device-code: the URL the user opens to enter the device user code */
  deviceVerificationUri: string;
}

export interface SteamAuthActions {
  setUsername: (value: string) => void;
  setPassword: (value: string) => void;
  setTwoFactorCode: (value: string) => void;
  setEmailCode: (value: string) => void;
  setUseManualCode: (value: boolean) => void;
  setNeedsTwoFactor: (value: boolean) => void;
  setWaitingForMobileConfirmation: (value: boolean) => void;
  setAuthorizationCode: (value: string) => void;
  handleAuthenticate: () => Promise<boolean>;
  resetAuthForm: () => void;
  cancelPendingRequest: () => void;
}
