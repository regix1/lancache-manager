# Grafana Integration Guide

Connect LanCache Manager to Grafana for advanced monitoring, alerting, and beautiful dashboards.

## Metrics Endpoints

LanCache Manager provides multiple endpoints for different monitoring needs:

| Endpoint | Format | Use Case | Authentication |
|----------|--------|----------|----------------|
| `/api/metrics` | Prometheus | Grafana, Prometheus scraping | Optional* |
| `/api/metrics/json` | JSON | Custom integrations, API access | Optional* |
| `/api/metrics/status` | JSON | Check endpoint availability | None |

Authentication: Set `Security__RequireAuthForMetrics=true` to require API key authentication.

## Available Metrics

### Service Metrics

Gaming platform-specific metrics for detailed service analysis:

| Metric | Description | Labels |
|--------|-------------|--------|
| `lancache_service_cache_hit_bytes` | Bytes served from cache | `service` (steam, epic, origin, etc.) |
| `lancache_service_cache_miss_bytes` | Bytes downloaded from origin | `service` |
| `lancache_service_hit_ratio` | Cache hit ratio (0-1) | `service` |
| `lancache_service_download_count` | Total downloads per service | `service` |

### Client Metrics
Individual client statistics for identifying heavy users:

| Metric | Description | Labels |
|--------|-------------|--------|
| `lancache_client_cache_hit_bytes` | Cache hits per client | `client_ip` |
| `lancache_client_cache_miss_bytes` | Cache misses per client | `client_ip` |
| `lancache_client_total_downloads` | Total downloads per client | `client_ip` |

### System Metrics
Overall system performance and health indicators:

| Metric | Description | Labels |
|--------|-------------|--------|
| `lancache_cache_size_bytes` | Total cache disk usage | None |
| `lancache_total_bandwidth_saved_bytes` | Cumulative bandwidth saved | None |
| `lancache_active_clients_total` | Number of active clients | None |

## Prometheus Setup

### Basic Configuration
```yaml
# prometheus.yml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: '/api/metrics'
```

### With Authentication
If `Security__RequireAuthForMetrics=true`:
```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    headers:
      X-Api-Key: 'your-api-key-here'
    metrics_path: '/api/metrics'
```

### Docker Compose Integration
```yaml
# Add to your existing docker-compose.yml
services:
  prometheus:
    image: prom/prometheus:latest
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    command:
      - '--config.file=/etc/prometheus/prometheus.yml'
      - '--storage.tsdb.path=/prometheus'
```

## Grafana Dashboard Setup

### 1. Add Data Source
1. Configuration → Data Sources → Add data source
2. Select Prometheus
3. Set URL: `http://prometheus:9090` (or your Prometheus URL)
4. Click Save & Test

### 2. Import Dashboard
1. Dashboard → Import
2. Upload the dashboard JSON from the `grafana-dashboards/` folder
3. Select your Prometheus data source
4. Click Import

### 3. Key Dashboard Panels

#### Overall Hit Rate
```promql
sum(rate(lancache_service_cache_hit_bytes[5m])) /
(sum(rate(lancache_service_cache_hit_bytes[5m])) +
 sum(rate(lancache_service_cache_miss_bytes[5m]))) * 100
```

#### Bandwidth Saved
```promql
lancache_total_bandwidth_saved_bytes
```

#### Top Clients by Traffic
```promql
topk(10, sum by (client_ip) (
  rate(lancache_client_cache_hit_bytes[1h]) +
  rate(lancache_client_cache_miss_bytes[1h])
))
```

#### Service Distribution
```promql
sum by (service) (lancache_service_download_count)
```

#### Active Clients Over Time
```promql
lancache_active_clients_total
```

#### Cache Efficiency by Service
```promql
sum by (service) (rate(lancache_service_cache_hit_bytes[5m])) /
(sum by (service) (rate(lancache_service_cache_hit_bytes[5m])) +
 sum by (service) (rate(lancache_service_cache_miss_bytes[5m]))) * 100
```

## Alert Rules

### Critical Alerts
```yaml
groups:
  - name: lancache-critical
    rules:
      - alert: CacheDiskFull
        expr: lancache_cache_free_bytes < 10737418240  # 10GB
        for: 5m
        labels:
          severity: critical
        annotations:
          summary: "LanCache disk space critically low"
          description: "Cache disk has less than 10GB free space"

      - alert: LancacheDown
        expr: up{job="lancache-manager"} == 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: "LanCache Manager is down"
          description: "LanCache Manager has been down for more than 2 minutes"
```

### Warning Alerts
```yaml
  - name: lancache-warnings
    rules:
      - alert: LowHitRate
        expr: (sum(rate(lancache_service_cache_hit_bytes[30m])) / (sum(rate(lancache_service_cache_hit_bytes[30m])) + sum(rate(lancache_service_cache_miss_bytes[30m])))) < 0.5
        for: 30m
        labels:
          severity: warning
        annotations:
          summary: "LanCache hit ratio below 50%"
          description: "Cache hit ratio has been below 50% for 30 minutes"

      - alert: HighClientLoad
        expr: lancache_active_clients_total > 100
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: "High number of active clients"
          description: "More than 100 active clients detected"
```

## Advanced Configuration

### Custom Dashboard Variables
Add these variables to make dashboards more dynamic:

- **Time Range**: `$__timeFilter()`
- **Service Filter**: `label_values(lancache_service_cache_hit_bytes, service)`
- **Client Filter**: `label_values(lancache_client_cache_hit_bytes, client_ip)`

### Performance Optimization
```yaml
# Prometheus configuration for large deployments
global:
  scrape_interval: 30s      # Longer interval for better performance
  evaluation_interval: 30s  # Alert evaluation frequency

scrape_configs:
  - job_name: 'lancache-manager'
    scrape_interval: 30s
    scrape_timeout: 10s
    metrics_path: '/api/metrics'
    static_configs:
      - targets: ['lancache-manager:80']
```

## Troubleshooting

| Problem | Solution | Check |
|---------|----------|-------|
| **No data in Grafana** | Verify Prometheus is scraping successfully | Visit `/api/metrics` directly |
| **Authentication errors** | Add API key to Prometheus headers | Check `Security__RequireAuthForMetrics` setting |
| **Slow performance** | Increase scrape interval to 30s+ | Monitor Prometheus resource usage |
| **Missing metrics** | Ensure LanCache Manager is processing logs | Check Management tab for log processing status |
| **Dashboard not loading** | Verify data source configuration | Test Prometheus connection in Grafana |

### Verification Commands
```bash
# Test metrics endpoint directly
curl http://localhost:8080/api/metrics

# Check if authentication is required
curl http://localhost:8080/api/metrics/status

# Test with API key
curl -H "X-Api-Key: your-key" http://localhost:8080/api/metrics

# Verify Prometheus is scraping
curl http://localhost:9090/api/v1/targets
```
