export interface MetricsSecurityResponse {
  requiresAuthentication: boolean;
  source: 'ui' | 'config' | 'default';
  canToggle: boolean;
  envVarValue: boolean;
}
