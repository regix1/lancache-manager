# Grafana Integration Guide

LANCache Manager provides comprehensive Prometheus metrics that can be visualized in Grafana for advanced monitoring and analytics.

## Table of Contents
- [Overview](#overview)
- [Prerequisites](#prerequisites)
- [Metrics Endpoints](#metrics-endpoints)
- [Available Metrics](#available-metrics)
- [Grafana Setup](#grafana-setup)
- [Dashboard Configuration](#dashboard-configuration)
- [Example Queries](#example-queries)
- [Alerting](#alerting)
- [Troubleshooting](#troubleshooting)

## Overview

LANCache Manager exposes metrics in Prometheus format, enabling powerful visualization and monitoring capabilities through Grafana. This integration allows you to:

- Track cache performance over time
- Monitor bandwidth savings
- Analyze service usage patterns
- Set up alerts for cache issues
- Create custom dashboards for your needs

## Prerequisites

### Required Components
- LANCache Manager running with metrics enabled
- Prometheus server for metrics collection
- Grafana instance for visualization
- Network connectivity between components

### Optional Components
- AlertManager for advanced alerting
- Loki for log aggregation
- Tempo for distributed tracing

## Metrics Endpoints

LANCache Manager provides multiple endpoints for metrics consumption:

### Primary Endpoints
| Endpoint | Format | Authentication | Description |
|----------|--------|----------------|-------------|
| `/api/metrics` | Prometheus | Optional | Standard Prometheus text format |
| `/api/metrics/prometheus` | Prometheus | Optional | Alternative Prometheus endpoint |
| `/api/metrics/json` | JSON | Optional | JSON format for custom integrations |
| `/api/metrics/grafana` | JSON | Optional | Grafana-optimized JSON format |

### Authentication Configuration
By default, metrics endpoints are public. To require authentication:

```yaml
# docker-compose.yml
environment:
  - Security__RequireAuthForMetrics=true
```

When enabled, include the API key in Prometheus configuration:
```yaml
# prometheus.yml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    authorization:
      credentials: 'lm_your-api-key-here'
```

## Available Metrics

### Service Metrics
| Metric | Type | Description | Labels |
|--------|------|-------------|--------|
| `lancache_service_cache_hit_bytes` | Counter | Total bytes served from cache | `service` |
| `lancache_service_cache_miss_bytes` | Counter | Total bytes fetched from origin | `service` |
| `lancache_service_hit_ratio` | Gauge | Current cache hit ratio (0-1) | `service` |
| `lancache_service_download_count` | Counter | Total number of downloads | `service` |
| `lancache_service_active_downloads` | Gauge | Currently active downloads | `service` |

### Client Metrics
| Metric | Type | Description | Labels |
|--------|------|-------------|--------|
| `lancache_client_cache_hit_bytes` | Counter | Cache hits per client | `client_ip` |
| `lancache_client_cache_miss_bytes` | Counter | Cache misses per client | `client_ip` |
| `lancache_client_total_downloads` | Counter | Downloads per client | `client_ip` |
| `lancache_client_active` | Gauge | Active client indicator (0/1) | `client_ip` |
| `lancache_client_last_seen` | Gauge | Unix timestamp of last activity | `client_ip` |

### Cache Metrics
| Metric | Type | Description | Labels |
|--------|------|-------------|--------|
| `lancache_cache_size_bytes` | Gauge | Total cache size on disk | - |
| `lancache_cache_used_bytes` | Gauge | Used cache space | - |
| `lancache_cache_free_bytes` | Gauge | Available cache space | - |
| `lancache_cache_file_count` | Gauge | Number of cached files | - |
| `lancache_cache_directory_count` | Gauge | Number of cache directories | - |

### System Metrics
| Metric | Type | Description | Labels |
|--------|------|-------------|--------|
| `lancache_total_bandwidth_saved_bytes` | Counter | Total bandwidth saved | - |
| `lancache_total_cache_hit_ratio` | Gauge | Overall hit ratio | - |
| `lancache_active_downloads_total` | Gauge | Total active downloads | - |
| `lancache_active_clients_total` | Gauge | Total active clients | - |
| `lancache_database_size_bytes` | Gauge | SQLite database size | - |
| `lancache_log_position` | Gauge | Current log file position | - |
| `lancache_log_size_bytes` | Gauge | Access log file size | - |
| `lancache_uptime_seconds` | Counter | Application uptime | - |

### Performance Metrics
| Metric | Type | Description | Labels |
|--------|------|-------------|--------|
| `lancache_log_processing_rate` | Gauge | Log entries per second | - |
| `lancache_log_processing_lag_seconds` | Gauge | Processing delay | - |
| `lancache_api_request_duration_seconds` | Histogram | API response times | `endpoint`, `method` |
| `lancache_api_request_total` | Counter | Total API requests | `endpoint`, `method`, `status` |

## Grafana Setup

### Step 1: Add Prometheus Data Source

1. Navigate to **Configuration → Data Sources** in Grafana
2. Click **Add data source**
3. Select **Prometheus**
4. Configure the connection:
   ```yaml
   Name: LANCache Metrics
   URL: http://prometheus:9090
   Access: Server (default)
   ```
5. Click **Save & Test**

### Step 2: Import Dashboard

#### Option A: Use Dashboard JSON
1. Download the dashboard template from the repository
2. Navigate to **Dashboards → Import**
3. Upload the JSON file or paste its contents
4. Select the Prometheus data source
5. Click **Import**

#### Option B: Create Custom Dashboard
1. Navigate to **Dashboards → New Dashboard**
2. Add panels for desired metrics
3. Configure visualizations
4. Save the dashboard

### Step 3: Configure Refresh Rate
Set appropriate refresh intervals:
- Real-time monitoring: 5-10 seconds
- Standard monitoring: 30-60 seconds
- Historical analysis: 5+ minutes

## Dashboard Configuration

### Recommended Panels

#### Cache Performance Overview
```promql
# Cache Hit Ratio
sum(rate(lancache_service_cache_hit_bytes[5m])) /
(sum(rate(lancache_service_cache_hit_bytes[5m])) +
 sum(rate(lancache_service_cache_miss_bytes[5m])))

# Bandwidth Saved
sum(lancache_total_bandwidth_saved_bytes)

# Cache Size
lancache_cache_size_bytes / (1024^3)  # Convert to GB
```

#### Service Distribution
```promql
# Downloads by Service (Pie Chart)
sum by (service) (lancache_service_download_count)

# Bandwidth by Service (Bar Chart)
sum by (service) (rate(lancache_service_cache_hit_bytes[1h]))
```

#### Client Activity
```promql
# Top Clients by Bandwidth
topk(10, sum by (client_ip) (
  rate(lancache_client_cache_hit_bytes[1h])
))

# Active Clients Over Time
lancache_active_clients_total
```

#### Real-time Monitoring
```promql
# Current Download Speed
sum(rate(lancache_service_cache_hit_bytes[1m])) * 8 / 1024^2  # Mbps

# Active Downloads
lancache_active_downloads_total
```

### Panel Types and Best Uses

| Panel Type | Best For | Example Metrics |
|------------|----------|-----------------|
| Stat | Single values | Total cache size, Hit ratio |
| Graph | Time series | Bandwidth over time |
| Gauge | Current values | Cache usage percentage |
| Bar gauge | Comparisons | Service distribution |
| Table | Detailed data | Client statistics |
| Pie chart | Proportions | Service breakdown |
| Heatmap | Patterns | Request distribution |

## Example Queries

### Performance Analysis
```promql
# Average cache hit ratio over last 24h
avg_over_time(lancache_total_cache_hit_ratio[24h])

# Peak bandwidth usage
max_over_time(sum(rate(lancache_service_cache_hit_bytes[5m]))[24h:5m])

# Cache efficiency by service
sum by (service) (lancache_service_cache_hit_bytes) /
sum by (service) (lancache_service_cache_hit_bytes + lancache_service_cache_miss_bytes)
```

### Capacity Planning
```promql
# Cache growth rate (GB/day)
rate(lancache_cache_size_bytes[1d]) / (1024^3)

# Projected cache full date
predict_linear(lancache_cache_free_bytes[7d], 86400 * 30) < 0

# Storage efficiency
lancache_total_bandwidth_saved_bytes / lancache_cache_size_bytes
```

### Client Analysis
```promql
# Client diversity (unique clients)
count(count by (client_ip) (lancache_client_total_downloads))

# Heavy users (top 5%)
topk(5, sum by (client_ip) (lancache_client_cache_hit_bytes))

# New vs returning clients
count(increase(lancache_client_total_downloads[1h]) > 0)
```

### Service Patterns
```promql
# Service usage trends
sum by (service) (rate(lancache_service_download_count[1h]))

# Service cache effectiveness
(sum by (service) (lancache_service_cache_hit_bytes) /
 sum by (service) (lancache_service_cache_hit_bytes + lancache_service_cache_miss_bytes)) * 100

# Peak service times
max_over_time(sum by (service) (rate(lancache_service_cache_hit_bytes[5m]))[24h:1h])
```

## Alerting

### Alert Rules Configuration

Create alert rules in Prometheus or Grafana:

#### Critical Alerts
```yaml
groups:
  - name: lancache_critical
    rules:
      - alert: CacheDiskFull
        expr: lancache_cache_free_bytes < 10737418240  # Less than 10GB
        for: 5m
        annotations:
          summary: "Cache disk space critically low"
          description: "Only {{ $value | humanize }} free space remaining"

      - alert: NoActiveClients
        expr: lancache_active_clients_total == 0
        for: 1h
        annotations:
          summary: "No clients connected for 1 hour"

      - alert: CacheHitRateLow
        expr: lancache_total_cache_hit_ratio < 0.5
        for: 30m
        annotations:
          summary: "Cache hit ratio below 50%"
          description: "Current ratio: {{ $value | humanizePercentage }}"
```

#### Warning Alerts
```yaml
      - alert: HighCacheGrowth
        expr: rate(lancache_cache_size_bytes[1h]) > 10737418240  # 10GB/hour
        for: 15m
        annotations:
          summary: "Rapid cache growth detected"

      - alert: LogProcessingLag
        expr: lancache_log_processing_lag_seconds > 60
        for: 10m
        annotations:
          summary: "Log processing falling behind"

      - alert: DatabaseSizeLarge
        expr: lancache_database_size_bytes > 1073741824  # 1GB
        annotations:
          summary: "Database size exceeds 1GB"
```

### Notification Channels

Configure alerting channels in Grafana:

1. **Email**: For non-urgent alerts
2. **Slack/Discord**: For team notifications
3. **PagerDuty**: For critical issues
4. **Webhook**: For custom integrations

Example Slack configuration:
```json
{
  "url": "https://hooks.slack.com/services/YOUR/WEBHOOK/URL",
  "username": "LANCache Alerts",
  "icon_emoji": ":warning:",
  "channel": "#monitoring"
}
```

## Troubleshooting

### Common Issues

#### No Data in Grafana
1. Verify LANCache Manager is running:
   ```bash
   curl http://lancache-manager/api/metrics
   ```

2. Check Prometheus targets:
   - Navigate to http://prometheus:9090/targets
   - Verify lancache-manager is UP

3. Test data source in Grafana:
   - Go to Configuration → Data Sources
   - Click your Prometheus source
   - Click "Save & Test"

#### Incorrect Metrics Values
1. Check timezone configuration:
   ```yaml
   environment:
     - TZ=America/Chicago
   ```

2. Verify metric calculation:
   ```bash
   # Direct query to LANCache Manager
   curl http://lancache-manager/api/metrics | grep lancache_
   ```

3. Check Prometheus retention:
   ```yaml
   # prometheus.yml
   global:
     scrape_interval: 15s
     evaluation_interval: 15s
   ```

#### Authentication Issues
If using `Security__RequireAuthForMetrics=true`:

1. Verify API key in Prometheus config:
   ```yaml
   authorization:
     credentials: 'lm_your-actual-key'
   ```

2. Test with curl:
   ```bash
   curl -H "X-Api-Key: lm_your-key" http://lancache-manager/api/metrics
   ```

#### Performance Issues
1. Adjust scrape interval:
   ```yaml
   scrape_interval: 30s  # Increase from default 15s
   ```

2. Optimize queries:
   - Use recording rules for complex queries
   - Limit time ranges in dashboards
   - Use appropriate aggregation functions

3. Configure retention policies:
   ```yaml
   # prometheus.yml
   storage.tsdb.retention.time: 30d
   storage.tsdb.retention.size: 10GB
   ```

### Debug Commands

```bash
# Check metric output
curl http://lancache-manager/api/metrics | head -50

# Verify JSON metrics
curl http://lancache-manager/api/metrics/json | jq .

# Test Prometheus query
curl http://prometheus:9090/api/v1/query?query=lancache_cache_size_bytes

# Check Grafana datasource
curl -H "Authorization: Bearer YOUR_GRAFANA_TOKEN" \
  http://grafana:3000/api/datasources

# View recent alerts
curl http://prometheus:9090/api/v1/alerts
```

## Best Practices

### Dashboard Design
1. **Group related metrics** in rows
2. **Use consistent time ranges** across panels
3. **Include documentation** in text panels
4. **Set meaningful thresholds** for gauges
5. **Use variables** for dynamic dashboards

### Query Optimization
1. **Use recording rules** for expensive queries
2. **Aggregate at source** when possible
3. **Limit cardinality** of labels
4. **Cache dashboard queries** appropriately
5. **Use incremental counters** over gauges

### Monitoring Strategy
1. **Define SLIs/SLOs** for cache performance
2. **Implement tiered alerting** (warning/critical)
3. **Document runbooks** for alerts
4. **Regular review** of metrics and thresholds
5. **Capacity planning** based on trends

## Additional Resources

- [Prometheus Documentation](https://prometheus.io/docs/)
- [Grafana Documentation](https://grafana.com/docs/)
- [PromQL Tutorial](https://prometheus.io/docs/prometheus/latest/querying/basics/)
- [Grafana Dashboard Best Practices](https://grafana.com/docs/grafana/latest/best-practices/dashboard-management/)
- [LANCache Manager API Documentation](../README.md#api-documentation)