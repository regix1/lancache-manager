# LanCache Manager Documentation

Welcome to the LanCache Manager wiki! This folder contains all the documentation for configuring and optimizing your LanCache Manager installation.

## Documentation Index

### Configuration Guides

- **[Docker Environment Reference](DOCKER_ENVIRONMENT_REFERENCE.md)** - Complete list of all Docker environment variables and their usage
- **[Performance Tuning](PERFORMANCE_TUNING.md)** - Guide for optimizing performance for different deployment sizes
- **[Claude Development Notes](CLAUDE.md)** - Development notes and instructions for AI-assisted development

### Quick Links

- **Main README** - See [../README.md](../README.md) for installation and basic setup
- **Docker Compose Example** - See [../docker-compose.yml](../docker-compose.yml) for a working configuration

## Quick Start Configuration Examples

### Home Network (1-10 clients)
See [PERFORMANCE_TUNING.md](PERFORMANCE_TUNING.md#small-home-network-1-10-clients)

### LAN Party (10-50 clients)
See [PERFORMANCE_TUNING.md](PERFORMANCE_TUNING.md#medium-lan-party-10-50-clients)

### Large Event (50+ clients)
See [PERFORMANCE_TUNING.md](PERFORMANCE_TUNING.md#large-deployment-50-200-clients)

## Environment Variables Quick Reference

### Essential Settings
```yaml
- PUID=1000                             # User ID
- PGID=1000                             # Group ID
- TZ=America/Chicago                    # Timezone
- LanCache__LogPath=/logs/access.log    # Log file path
- LanCache__CachePath=/cache            # Cache directory
```

### Security Options
```yaml
- Security__EnableAuthentication=true   # Require auth for Management tab
- Security__RequireAuthForMetrics=false # Require auth for Prometheus metrics
```

### Performance Tuning
```yaml
- LanCache__ChannelCapacity=100000      # Queue size
- LanCache__BatchSize=5000               # Batch size
- LanCache__BatchTimeoutMs=500          # Batch timeout
- LanCache__ConsumerCount=4             # Parallel consumers
- LanCache__ParserParallelism=8         # Parallel parsers
- LanCache__UseHighThroughputMode=false # Bulk processing mode
```

## Getting Help

1. Check the relevant documentation file
2. Review the main [README.md](../README.md)
3. Open an issue on GitHub

## Contributing

Feel free to submit PRs to improve the documentation!