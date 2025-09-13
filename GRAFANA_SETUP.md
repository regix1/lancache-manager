# Grafana Setup for Lancache Manager

## Overview
Lancache Manager provides real-time metrics that can be visualized in Grafana. The application exposes metrics in Prometheus format that Grafana can scrape and display.

## Security Configuration

### Authentication Options

The metrics endpoints support two modes:

1. **Public Access (Default)**: No authentication required
   - Default setting: metrics are publicly accessible
   - Ideal for internal networks or when Grafana runs on the same host

2. **API Key Authentication**: Secure access with API key
   - Enable via Docker: `Security__RequireAuthForMetrics=true`
   - Or in `appsettings.json`: `"RequireAuthForMetrics": true`
   - Required header: `X-Api-Key: your-api-key-here`
   - Get your API key from the container logs or `/data/api_key.txt`

### Quick Setup with Docker

```yaml
# docker-compose.yml
services:
  lancache-manager:
    environment:
      # Enable authentication for metrics
      - Security__RequireAuthForMetrics=true
```

## Available Endpoints

### Prometheus Metrics
- **URL**: `http://your-server:5000/api/metrics` or `http://your-server:5000/api/metrics/prometheus`
- **Format**: Prometheus text format
- **Use Case**: Best for Grafana with Prometheus data source
- **Authentication**: Optional (configurable)

### JSON Metrics
- **URL**: `http://your-server:5000/api/metrics/json`
- **Format**: JSON
- **Use Case**: For custom integrations or JSON data source
- **Authentication**: Optional (configurable)

## How Grafana Auto-Updates

Yes, Grafana can auto-update with live data! Here's how:

1. **Data Source Connection**: Grafana connects to your API endpoints
2. **Refresh Intervals**: Set dashboard refresh rates (5s, 10s, 30s, etc.)
3. **Real-time Queries**: Each refresh queries the latest data from your API

## Setting Up Grafana

### Option 1: Using Prometheus (Recommended)

1. **Install Prometheus**
   ```bash
   # Download and install Prometheus
   # Configure prometheus.yml:
   ```
   
2. **Configure Prometheus** (`prometheus.yml`):

   **Without Authentication (Default):**
   ```yaml
   global:
     scrape_interval: 15s
   
   scrape_configs:
     - job_name: 'lancache'
       static_configs:
         - targets: ['localhost:5000']
       metrics_path: '/api/metrics'
   ```

   **With API Key Authentication:**
   ```yaml
   global:
     scrape_interval: 15s
   
   scrape_configs:
     - job_name: 'lancache'
       static_configs:
         - targets: ['localhost:5000']
       metrics_path: '/api/metrics'
       authorization:
         type: 'apikey'
         credentials: 'lm_your-api-key-here'
       # Or use custom header:
       params:
         headers: ['X-Api-Key: lm_your-api-key-here']
   ```

3. **Add Prometheus Data Source in Grafana**:
   - Go to Configuration → Data Sources
   - Add new data source → Prometheus
   - URL: `http://localhost:9090`
   - Save & Test

### Option 2: Direct API Connection (JSON)

1. **Install JSON Data Source Plugin**:
   ```bash
   grafana-cli plugins install simpod-json-datasource
   ```

2. **Configure JSON Data Source**:
   
   **Without Authentication (Default):**
   - Go to Configuration → Data Sources
   - Add new data source → JSON
   - URL: `http://localhost:5000/api/metrics/json`
   - Save & Test

   **With API Key Authentication:**
   - Go to Configuration → Data Sources
   - Add new data source → JSON
   - URL: `http://localhost:5000/api/metrics/json`
   - Add Custom HTTP Headers:
     - Header: `X-Api-Key`
     - Value: `lm_your-api-key-here`
   - Save & Test

## Available Metrics

### Service Metrics
- `lancache_service_cache_hit_bytes` - Cache hits per service (Steam, Epic, etc.)
- `lancache_service_cache_miss_bytes` - Cache misses per service
- `lancache_service_hit_ratio` - Cache hit ratio (0-1)
- `lancache_service_download_count` - Total downloads per service

### Client Metrics
- `lancache_client_cache_hit_bytes` - Cache hits per client IP
- `lancache_client_cache_miss_bytes` - Cache misses per client IP

### Cache Storage Metrics
- `lancache_cache_total_bytes` - Total cache capacity
- `lancache_cache_used_bytes` - Used cache space
- `lancache_cache_free_bytes` - Available cache space
- `lancache_cache_usage_ratio` - Usage percentage (0-1)
- `lancache_cache_file_count` - Number of cached files

### System Metrics
- `lancache_active_downloads` - Currently active downloads
- `lancache_unique_clients` - Total unique clients
- `lancache_bandwidth_saved_bytes` - Total bandwidth saved

## Creating Grafana Dashboards

### Example Panel Queries

1. **Bandwidth Saved Over Time**:
   ```promql
   rate(lancache_bandwidth_saved_bytes[5m])
   ```

2. **Cache Hit Ratio by Service**:
   ```promql
   lancache_service_hit_ratio
   ```

3. **Active Downloads**:
   ```promql
   lancache_active_downloads
   ```

4. **Cache Usage Percentage**:
   ```promql
   lancache_cache_usage_ratio * 100
   ```

5. **Top Services by Traffic**:
   ```promql
   topk(5, lancache_service_cache_hit_bytes + lancache_service_cache_miss_bytes)
   ```

## Dashboard Configuration

### Recommended Settings
- **Auto-refresh**: 10s or 30s for real-time monitoring
- **Time range**: Last 1 hour for detailed view
- **Variables**: Add service and client filters

### Sample Dashboard JSON
You can import this basic dashboard to get started:

```json
{
  "dashboard": {
    "title": "Lancache Manager",
    "panels": [
      {
        "title": "Bandwidth Saved",
        "targets": [
          {
            "expr": "lancache_bandwidth_saved_bytes"
          }
        ]
      },
      {
        "title": "Cache Hit Ratio",
        "targets": [
          {
            "expr": "lancache_service_hit_ratio"
          }
        ]
      },
      {
        "title": "Active Downloads",
        "targets": [
          {
            "expr": "lancache_active_downloads"
          }
        ]
      },
      {
        "title": "Cache Usage",
        "targets": [
          {
            "expr": "lancache_cache_usage_ratio * 100"
          }
        ]
      }
    ],
    "refresh": "10s",
    "time": {
      "from": "now-1h",
      "to": "now"
    }
  }
}
```

## CORS Configuration

If Grafana is on a different server, ensure CORS is configured in your API:

The Lancache Manager API already includes CORS configuration in `Program.cs`. If you need to adjust it, modify the CORS policy there.

## Troubleshooting

### Metrics Not Updating
1. Check API endpoint is accessible: `curl http://localhost:5000/api/metrics`
2. Verify Prometheus is scraping: Check Prometheus targets page
3. Check Grafana data source connection

### No Data Points
1. Ensure Lancache Manager is collecting data
2. Check time range in Grafana
3. Verify query syntax

### Authentication Issues
If your API requires authentication, configure it in:
- Prometheus: Use `bearer_token` or `basic_auth` in scrape config
- Grafana JSON: Add headers in data source configuration

## API Key Management

### Finding Your API Key
1. Check container logs when starting: `docker logs lancache-manager`
2. Or read from file: `cat /data/api_key.txt`
3. The key starts with `lm_` prefix

### Enabling Authentication for Metrics

**Option 1: Docker Environment Variable (Recommended)**
```bash
docker run -e Security__RequireAuthForMetrics=true ...
# Or in docker-compose.yml as shown above
```

**Option 2: Configuration File**
1. Edit `/data/appsettings.json` or mount a custom config
2. Set `"RequireAuthForMetrics": true` under Security section
3. Restart the container

### Using API Key in Different Tools

**curl:**
```bash
curl -H "X-Api-Key: lm_your-key-here" http://localhost:5000/api/metrics
```

**wget:**
```bash
wget --header="X-Api-Key: lm_your-key-here" http://localhost:5000/api/metrics
```

**Python:**
```python
import requests
headers = {'X-Api-Key': 'lm_your-key-here'}
response = requests.get('http://localhost:5000/api/metrics', headers=headers)
```

## Performance Considerations

- Metrics endpoint is lightweight and cached
- Recommended scrape interval: 15-30 seconds
- For high-traffic environments, consider using Prometheus for data aggregation

## Need Help?

1. Check API is running: `http://localhost:5000/api/metrics`
2. Test with curl: `curl -H "Accept: text/plain" http://localhost:5000/api/metrics`
3. Check application logs for errors
4. Ensure database is accessible and contains data