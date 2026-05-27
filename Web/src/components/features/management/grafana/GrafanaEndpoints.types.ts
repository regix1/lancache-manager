export interface MetricsSecurityResponse {
  requiresAuthentication: boolean;
  source: 'ui' | 'config';
  canToggle: boolean;
  envVarValue: boolean;
}
