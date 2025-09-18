# Performance Tuning Guide

Optimize LanCache Manager for your specific deployment size and hardware configuration.

Important: These are estimated values based on testing. Adjust based on your specific hardware, network conditions, and actual usage patterns.

## Performance Settings Reference

| Variable | Default | Range | Purpose | Impact |
|----------|---------|-------|---------|--------|
| `ChannelCapacity` | 100000 | 1000-1000000 | Internal queue size for log entries | Memory usage |
| `BatchSize` | 5000 | 100-50000 | Database batch size for bulk operations | DB performance |
| `BatchTimeoutMs` | 500 | 100-5000 | Maximum wait time for batch completion | Real-time updates |
| `ConsumerCount` | 4 | 1-16 | Number of parallel log processors | CPU usage |
| `ParserParallelism` | 8 | 1-32 | Number of parallel log parsers | CPU usage |
| `UseHighThroughputMode` | false | true/false | Optimize for maximum throughput | Real-time vs batch |

## Deployment Size Configurations

Choose the configuration that matches your deployment size and expected load.

### Home Network (1-10 clients)
Hardware: 2GB RAM, 2 CPU cores | Peak Load: ~5K log lines/sec

```yaml
environment:
  - LanCache__ConsumerCount=2
  - LanCache__ParserParallelism=4
  - LanCache__BatchSize=1000
  - LanCache__ChannelCapacity=50000
  - LanCache__BatchTimeoutMs=1000
```

Characteristics: Real-time updates, low resource usage, single-family household.

### LAN Party (10-50 clients)
Hardware: 4GB RAM, 4 CPU cores | Peak Load: ~20K log lines/sec

```yaml
environment:
  - LanCache__ConsumerCount=4
  - LanCache__ParserParallelism=8
  - LanCache__BatchSize=5000
  - LanCache__ChannelCapacity=100000
  - LanCache__BatchTimeoutMs=500
```

Characteristics: Balanced performance and responsiveness, weekend gaming events.

### Large Event (50-200 clients)
Hardware: 8GB RAM, 8+ CPU cores | Peak Load: ~50K log lines/sec

```yaml
environment:
  - LanCache__ConsumerCount=8
  - LanCache__ParserParallelism=16
  - LanCache__BatchSize=10000
  - LanCache__ChannelCapacity=200000
  - LanCache__BatchTimeoutMs=300
  - LanCache__UseHighThroughputMode=true
```

Characteristics: High-performance mode, gaming conventions, large LAN parties.

### Enterprise/Event (200+ clients)
Hardware: 16GB+ RAM, 16+ CPU cores | Peak Load: ~100K+ log lines/sec

```yaml
environment:
  - LanCache__ConsumerCount=16
  - LanCache__ParserParallelism=32
  - LanCache__BatchSize=20000
  - LanCache__ChannelCapacity=500000
  - LanCache__BatchTimeoutMs=200
  - LanCache__UseHighThroughputMode=true
```

Characteristics: Maximum throughput, enterprise deployments, major gaming events.

## Performance Monitoring

### Real-time Monitoring
```bash
# Real-time performance statistics
curl http://localhost:8080/api/performance/stats

# Detailed performance metrics
curl http://localhost:8080/api/performance/metrics

# System health check
curl http://localhost:8080/api/health
```

### Monitor Container Resources
```bash
# View container resource usage
docker stats lancache-manager

# View real-time logs
docker logs -f lancache-manager
```

### Performance Monitoring Script
```bash
#!/bin/bash
echo "LanCache Manager Performance Monitor"
echo "==================================="
while true; do
  echo "$(date '+%Y-%m-%d %H:%M:%S')"
  curl -s http://localhost:8080/api/performance/stats | jq '.queueStats'
  echo "---"
  sleep 10
done
```

## Troubleshooting Guide

| Problem | Symptoms | Solution | Settings to Adjust |
|---------|----------|----------|-------------------|
| **High Memory Usage** | Container using >80% RAM | Reduce queue and batch sizes | `ChannelCapacity`, `BatchSize` |
| **Slow Log Processing** | Dashboard updates delayed | Increase parallel processing | `ConsumerCount`, `ParserParallelism` |
| **Dashboard Not Updating** | Real-time data missing | Disable high-throughput mode | `UseHighThroughputMode=false` |
| **Queue Overflow** | "Queue full" errors in logs | Increase queue capacity | `ChannelCapacity` |
| **Database Locked** | SQLite lock errors | Reduce concurrent operations | `ConsumerCount`, `BatchSize` |
| **High CPU Usage** | Container using >90% CPU | Reduce parallelism | `ParserParallelism`, `ConsumerCount` |

### Performance Optimization Steps

1. Start with defaults - Begin with recommended settings for your deployment size
2. Monitor first - Establish baseline performance metrics
3. Adjust gradually - Change one setting at a time
4. Test thoroughly - Verify stability under peak load
5. Document changes - Keep track of what works best

## Performance Expectations (Estimated)

| Deployment Size | Clients | Log Lines/sec | Memory Usage | CPU Usage | Storage/Day |
|----------------|---------|---------------|--------------|-----------|-------------|
| **🏠 Home** | 1-10 | ~5K | ~500MB | ~10% | ~100MB |
| **🎉 LAN Party** | 10-50 | ~20K | ~1GB | ~25% | ~500MB |
| **🏟️ Large Event** | 50-200 | ~50K | ~2GB | ~50% | ~2GB |
| **🏢 Enterprise** | 200+ | ~100K+ | ~4GB+ | ~80% | ~10GB+ |

Note: These are rough estimates. Actual performance varies significantly based on hardware specifications, network configuration, log complexity, and usage patterns.

## Optimization Best Practices

### General Guidelines
- ParserParallelism should typically be 2x ConsumerCount
- Lower BatchTimeoutMs for more real-time updates (higher CPU usage)
- Higher BatchTimeoutMs for better throughput (less real-time)
- UseHighThroughputMode only for events with >50 clients

### Memory Optimization
- Start with lower ChannelCapacity and increase if needed
- Reduce BatchSize if experiencing out-of-memory issues
- Monitor Docker container memory usage regularly

### CPU Optimization
- Match ConsumerCount to available CPU cores (up to 16)
- Adjust ParserParallelism based on CPU performance
- Use htop or similar tools to monitor CPU usage

### Storage Optimization
- Use SSD storage for better database performance
- Consider log rotation for the nginx access logs
- Monitor SQLite database size growth

## Hardware Recommendations

### Minimum Requirements
- **CPU**: 2 cores
- **RAM**: 2GB
- **Storage**: SSD with 10GB+ free space
- **Network**: 100Mbps

### Recommended Specifications
- **CPU**: 4-8 cores
- **RAM**: 4-8GB
- **Storage**: NVMe SSD with 50GB+ free space
- **Network**: 1Gbps

### High-Performance Setup
- **CPU**: 16+ cores
- **RAM**: 16GB+
- **Storage**: NVMe RAID with 100GB+ free space
- **Network**: 10Gbps
- **Additional**: Dedicated database server for enterprise setups

## Advanced Tuning

### Container-Level Optimizations
```yaml
# docker-compose.yml optimizations
services:
  lancache-manager:
    deploy:
      resources:
        limits:
          cpus: '8.0'
          memory: 8G
        reservations:
          cpus: '2.0'
          memory: 2G
    environment:
      # Add these for large deployments
      - DOTNET_SYSTEM_GC_SERVER=true
      - DOTNET_gcServer=1
```

### Database Optimizations
For very large deployments, consider:
- External PostgreSQL instead of SQLite
- Database connection pooling
- Dedicated database server