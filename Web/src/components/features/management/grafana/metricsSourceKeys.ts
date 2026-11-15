import type { MetricsSecurityResponse } from './GrafanaEndpoints.types';

/** The line that says where the `/metrics` access rule comes from, for each `source` the server sends. */
export const METRICS_SOURCE_KEYS: Record<MetricsSecurityResponse['source'], string> = {
  ui: 'management.grafana.metricsToggle.source.ui',
  config: 'management.grafana.metricsToggle.source.config',
  default: 'management.grafana.metricsToggle.source.default'
};
