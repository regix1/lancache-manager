# Documentation

## Configuration
- [Docker Environment Variables](DOCKER_ENVIRONMENT_REFERENCE.MD)
- [Performance Tuning](PERFORMANCE_TUNING.MD)
- [Development Notes](CLAUDE.MD)

## Quick Start

**Home (1-10 clients)**: Default settings
**LAN Party (10-50 clients)**: See [Performance Tuning](PERFORMANCE_TUNING.MD)
**Large Event (50+ clients)**: See [Performance Tuning](PERFORMANCE_TUNING.MD)

## Essential Variables

```yaml
TZ=America/Chicago
LanCache__LogPath=/logs/access.log
LanCache__CachePath=/cache
Security__EnableAuthentication=false  # Disable auth for local network
```

See [full environment reference](DOCKER_ENVIRONMENT_REFERENCE.MD) for all options.