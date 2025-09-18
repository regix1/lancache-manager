# LanCache Manager Documentation

Complete documentation for setting up, configuring, and optimizing your LanCache Manager deployment.

## Quick Start

### 5-Minute Setup
1. Install Docker and Docker Compose on your system
2. Create docker-compose.yml with your LanCache paths
3. Run `docker-compose up -d`
4. Access `http://localhost:8080`

### Deployment Sizes
| Type | Clients | Settings | RAM | CPU |
|------|---------|----------|-----|-----|
| Home | 1-10 | Default | 2GB | 2 cores |
| LAN Party | 10-50 | Default | 4GB | 4 cores |
| Large Event | 50-200 | Default | 8GB | 8 cores |
| Enterprise | 200+ | Default | 16GB+ | 16+ cores |

## Essential Configuration

### Minimum Required Variables
```yaml
# In your docker-compose.yml environment section:
TZ=America/Chicago                    # Your timezone
LanCache__LogPath=/logs/access.log    # Path to nginx access.log
LanCache__CachePath=/cache            # Path to cache directory
```

### Common Optional Settings
```yaml
# Security (recommended for production)
Security__EnableAuthentication=false        # Disable auth for local networks
Security__RequireAuthForMetrics=false      # Public Grafana metrics

# Logging behavior
LanCache__StartFromEndOfLog=true           # Start from current position
LanCache__ProcessHistoricalLogs=false     # Don't process old logs on startup
```

## Complete Documentation

### Configuration and Setup
- [Environment Variables](DOCKER_ENVIRONMENT_REFERENCE.MD) - Complete variable reference
- [Security Settings](DOCKER_ENVIRONMENT_REFERENCE.MD#security-settings) - Authentication and API keys

### Customization
- [Theming Guide](THEMING.MD) - Create custom themes and modify appearance
- [Community Themes](../community-themes/README.MD) - Download themes created by users

### Integrations
- [Grafana Integration](GRAFANA.MD) - Set up monitoring dashboards and alerts
- [API Reference](DOCKER_ENVIRONMENT_REFERENCE.MD#api-endpoints) - Available endpoints and usage

### Advanced Topics
- [Development Setup](../README.MD#development) - Build from source

## Common Use Cases

### Home Network (1-10 clients)
```yaml
# Minimal configuration - everything else is auto-detected
environment:
  - TZ=America/Chicago
  - LanCache__LogPath=/logs/access.log
  - LanCache__CachePath=/cache
  - Security__EnableAuthentication=false
```

### LAN Party (10-50 clients)
```yaml
# Default settings work great, just ensure proper resource allocation
environment:
  - TZ=America/Chicago
  - LanCache__LogPath=/logs/access.log
  - LanCache__CachePath=/cache
  - Security__EnableAuthentication=false
```
Resource allocation: 4GB RAM, 4 CPU cores

### Large Event (50+ clients)
```yaml
# Standard configuration
environment:
  - TZ=America/Chicago
  - LanCache__LogPath=/logs/access.log
  - LanCache__CachePath=/cache
  - Security__EnableAuthentication=false
```
Resource allocation: 8GB+ RAM, 8+ CPU cores

### Enterprise Deployment
```yaml
# High-security configuration
environment:
  - TZ=America/Chicago
  - LanCache__LogPath=/logs/access.log
  - LanCache__CachePath=/cache
  - Security__EnableAuthentication=true      # Require authentication
  - Security__RequireAuthForMetrics=true     # Secure Grafana metrics
```

## Quick Troubleshooting

| Problem | Solution |
|---------|----------|
| **No data showing** | Check log file path and permissions |
| **Authentication issues** | Set `Security__EnableAuthentication=false` for local networks |
| **Grafana no data** | Check `/api/metrics` endpoint accessibility |
| **Container won't start** | Verify volume mounts and file permissions |

## Quick Links

- [Main README](../README.MD) - Project overview and quick setup
- [Live Demo](http://localhost:8080) - Access your running instance
- [Report Issues](../../issues) - Bug reports and feature requests
- [Discussions](../../discussions) - Community support and questions
- [Themes](../community-themes/) - Custom theme collection

---

<div align="center">

Need help? Check the documentation or ask the community!

[Theming](THEMING.MD) • [Grafana Setup](GRAFANA.MD)

</div>