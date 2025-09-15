# Grafana Integration

## Metrics Endpoints

| Endpoint | Format | Description |
|----------|--------|-------------|
| `/api/metrics` | Prometheus | Standard text format |
| `/api/metrics/json` | JSON | JSON format |

Authentication: Set `Security__RequireAuthForMetrics=true` to require auth.

## Available Metrics

### Service Metrics
- `lancache_service_cache_hit_bytes` - Bytes from cache
- `lancache_service_cache_miss_bytes` - Bytes from origin
- `lancache_service_hit_ratio` - Hit ratio (0-1)
- `lancache_service_download_count` - Total downloads

### Client Metrics
- `lancache_client_cache_hit_bytes` - Per-client hits
- `lancache_client_cache_miss_bytes` - Per-client misses
- `lancache_client_total_downloads` - Per-client downloads

### System Metrics
- `lancache_cache_size_bytes` - Total cache size
- `lancache_total_bandwidth_saved_bytes` - Bandwidth saved
- `lancache_active_clients_total` - Active clients

## Prometheus Setup

```yaml
# prometheus.yml
scrape_configs:
  - job_name: 'lancache'
    static_configs:
      - targets: ['lancache-manager:80']
```

## Grafana Dashboard

### Import Dashboard
1. Configuration → Data Sources → Add Prometheus
2. Dashboards → Import → Upload JSON

### Key Queries

```promql
# Hit Ratio
sum(rate(lancache_service_cache_hit_bytes[5m])) /
(sum(rate(lancache_service_cache_hit_bytes[5m])) +
 sum(rate(lancache_service_cache_miss_bytes[5m])))

# Bandwidth Saved
lancache_total_bandwidth_saved_bytes

# Top Clients
topk(10, sum by (client_ip) (
  rate(lancache_client_cache_hit_bytes[1h])
))

# Service Distribution
sum by (service) (lancache_service_download_count)
```

## Alerts

```yaml
groups:
  - name: lancache
    rules:
      - alert: CacheDiskFull
        expr: lancache_cache_free_bytes < 10737418240
        for: 5m
        annotations:
          summary: "Cache disk < 10GB"

      - alert: LowHitRate
        expr: lancache_total_cache_hit_ratio < 0.5
        for: 30m
        annotations:
          summary: "Hit ratio < 50%"
```

## Troubleshooting

**No data**: Check `/api/metrics` endpoint
**Auth issues**: Verify API key in Prometheus config
**Performance**: Increase scrape_interval to 30s
