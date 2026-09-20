export type AuthStage =
  | 'start'
  | 'credentials'
  | 'two-factor'
  | 'email-code'
  | 'phone-approval'
  | 'authorization-code'
  | 'device-code';

export interface AuthStep {
  actionId: number;
  stage: AuthStage;
  challengeIds: Set<string>;
}

export function getAuthStage(credentialType: string | null | undefined): AuthStage | null {
  switch (credentialType) {
    case 'username':
    case 'password':
      return 'credentials';
    case '2fa':
      return 'two-factor';
    case 'steamguard':
      return 'email-code';
    case 'device-confirmation':
      return 'phone-approval';
    case 'authorization-url':
      return 'authorization-code';
    case 'device-code':
      return 'device-code';
    default:
      return null;
  }
}
