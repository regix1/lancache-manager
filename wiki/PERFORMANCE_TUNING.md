# Performance Tuning Guide

## Overview

LanCache Manager now includes advanced performance tuning options to handle high-volume log processing efficiently. These settings allow you to optimize for your specific deployment size and server capabilities.

## Performance Settings

All settings are configured via Docker environment variables in your `docker-compose.yml`:

### ChannelCapacity
**Default:** 100000
**Range:** 1000 - 1000000
**Purpose:** Size of the internal queues for log processing

```yaml
- LanCache__ChannelCapacity=100000
```

- **Small deployments (< 10 clients):** 10000
- **Medium deployments (10-50 clients):** 100000 (default)
- **Large deployments (50+ clients):** 500000

### BatchSize
**Default:** 5000
**Range:** 100 - 50000
**Purpose:** Number of log entries to process in a single database transaction

```yaml
- LanCache__BatchSize=5000
```

- **Small deployments:** 1000
- **Medium deployments:** 5000 (default)
- **Large deployments:** 10000-20000

Larger batches = Better throughput but higher memory usage and latency

### BatchTimeoutMs
**Default:** 500ms
**Range:** 100 - 5000
**Purpose:** Maximum time to wait before processing a partial batch

```yaml
- LanCache__BatchTimeoutMs=500
```

- **Real-time monitoring:** 100-200ms (faster updates)
- **Balanced:** 500ms (default)
- **High efficiency:** 1000-2000ms (better batching)

### ConsumerCount
**Default:** 4
**Range:** 1 - 16
**Purpose:** Number of parallel consumers processing parsed log entries

```yaml
- LanCache__ConsumerCount=4
```

- **2-4 CPU cores:** 2-4 consumers
- **6-8 CPU cores:** 4-8 consumers
- **12+ CPU cores:** 8-16 consumers

### ParserParallelism
**Default:** 8
**Range:** 1 - 32
**Purpose:** Number of parallel threads parsing raw log lines

```yaml
- LanCache__ParserParallelism=8
```

Should typically be 2x the ConsumerCount for optimal flow:
- **Small deployments:** 4
- **Medium deployments:** 8 (default)
- **Large deployments:** 16-32

### UseHighThroughputMode
**Default:** false
**Purpose:** Optimizes for maximum throughput at the cost of real-time updates

```yaml
- LanCache__UseHighThroughputMode=false
```

When enabled:
- ✅ Prioritizes throughput over latency
- ✅ Waits instead of dropping when queues are full
- ✅ Larger buffer sizes (131KB vs 8KB)
- ❌ Disables real-time SignalR updates
- ❌ May delay dashboard updates

## Recommended Configurations

### Small Home Network (1-10 clients)
```yaml
environment:
  - LanCache__ChannelCapacity=10000
  - LanCache__BatchSize=1000
  - LanCache__BatchTimeoutMs=200
  - LanCache__ConsumerCount=2
  - LanCache__ParserParallelism=4
  - LanCache__UseHighThroughputMode=false
```

### Medium LAN Party (10-50 clients)
```yaml
environment:
  - LanCache__ChannelCapacity=100000
  - LanCache__BatchSize=5000
  - LanCache__BatchTimeoutMs=500
  - LanCache__ConsumerCount=4
  - LanCache__ParserParallelism=8
  - LanCache__UseHighThroughputMode=false
```

### Large Deployment (50-200 clients)
```yaml
environment:
  - LanCache__ChannelCapacity=500000
  - LanCache__BatchSize=10000
  - LanCache__BatchTimeoutMs=1000
  - LanCache__ConsumerCount=8
  - LanCache__ParserParallelism=16
  - LanCache__UseHighThroughputMode=false
```

### Event/Convention (200+ clients)
```yaml
environment:
  - LanCache__ChannelCapacity=1000000
  - LanCache__BatchSize=20000
  - LanCache__BatchTimeoutMs=2000
  - LanCache__ConsumerCount=16
  - LanCache__ParserParallelism=32
  - LanCache__UseHighThroughputMode=true
```

## Monitoring Performance

### Via API Endpoint
Check current performance metrics:
```bash
curl http://localhost:8080/api/performance/metrics
```

Returns:
- Memory usage
- CPU metrics
- Queue depths
- Current configuration

### Queue Statistics
```bash
curl http://localhost:8080/api/performance/stats
```

Shows:
- `RawQueueCount`: Unparsed log lines waiting
- `ParsedQueueCount`: Parsed entries waiting for database
- `BatchBufferCount`: Current batch being built
- `ActiveConsumers`: Number of active consumer threads
- `ActiveParsers`: Number of active parser threads

## Troubleshooting

### High Memory Usage
- Reduce `ChannelCapacity`
- Reduce `BatchSize`
- Decrease `ConsumerCount`

### Logs Processing Too Slowly
- Increase `ConsumerCount` (if CPU available)
- Increase `ParserParallelism`
- Increase `BatchSize`
- Enable `UseHighThroughputMode`

### Dashboard Not Updating
- Decrease `BatchTimeoutMs`
- Ensure `UseHighThroughputMode=false`
- Check queue stats for backlogs

### Queue Overflow Messages
- Increase `ChannelCapacity`
- Increase `ConsumerCount`
- Enable `UseHighThroughputMode`

## Performance Expectations

With optimal settings:

| Deployment Size | Log Lines/sec | Memory Usage | CPU Usage |
|----------------|---------------|--------------|-----------|
| Small (10 clients) | 1,000-5,000 | 200-500 MB | 5-10% |
| Medium (50 clients) | 5,000-20,000 | 500MB-1GB | 10-25% |
| Large (200 clients) | 20,000-50,000 | 1-2GB | 25-50% |
| Event (500+ clients) | 50,000-100,000 | 2-4GB | 50-80% |

## Best Practices

1. **Start with defaults** - They work well for most deployments
2. **Monitor queue depths** - If consistently > 50% capacity, increase capacity
3. **Balance consumers and parsers** - Parsers should be ~2x consumers
4. **Adjust batch timeout** - Lower for real-time, higher for efficiency
5. **Use high throughput mode** - Only for large events where real-time isn't critical
6. **Scale gradually** - Increase settings incrementally while monitoring

## Hardware Recommendations

### Minimum
- 2 CPU cores
- 2GB RAM
- SSD for database

### Recommended
- 4-8 CPU cores
- 4-8GB RAM
- NVMe SSD for database

### Large Deployments
- 16+ CPU cores
- 16GB+ RAM
- NVMe SSD RAID for database
- 10Gbps network

## Example Monitoring Script

```bash
#!/bin/bash
# Monitor performance every 5 seconds

while true; do
  clear
  echo "=== LanCache Manager Performance ==="
  curl -s http://localhost:8080/api/performance/stats | jq '.'
  echo ""
  echo "Press Ctrl+C to exit"
  sleep 5
done
```

## Notes

- All settings take effect on container restart
- Database performance is critical - use SSD/NVMe
- Network I/O can be a bottleneck in large deployments
- Consider using multiple LanCache Manager instances for very large events