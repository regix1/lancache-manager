# Performance Tuning

## Settings

| Variable | Default | Range | Purpose |
|----------|---------|-------|----------|
| `ChannelCapacity` | 100000 | 1000-1000000 | Queue size |
| `BatchSize` | 5000 | 100-50000 | DB batch size |
| `BatchTimeoutMs` | 500 | 100-5000 | Batch timeout |
| `ConsumerCount` | 4 | 1-16 | Parallel consumers |
| `ParserParallelism` | 8 | 1-32 | Parallel parsers |
| `UseHighThroughputMode` | false | - | Bulk mode |

## Configurations

### Home (1-10 clients)
```yaml
LanCache__ConsumerCount=2
LanCache__ParserParallelism=4
LanCache__BatchSize=1000
```

### LAN Party (10-50 clients)
```yaml
LanCache__ConsumerCount=4
LanCache__ParserParallelism=8
LanCache__BatchSize=5000
```

### Large (50-200 clients)
```yaml
LanCache__ConsumerCount=8
LanCache__ParserParallelism=16
LanCache__BatchSize=10000
```

### Event (200+ clients)
```yaml
LanCache__ConsumerCount=16
LanCache__ParserParallelism=32
LanCache__BatchSize=20000
LanCache__UseHighThroughputMode=true
```

## Monitoring

```bash
# Performance metrics
curl http://localhost:8080/api/performance/metrics

# Queue stats
curl http://localhost:8080/api/performance/stats
```

## Troubleshooting

**High memory**: Reduce ChannelCapacity and BatchSize
**Slow processing**: Increase ConsumerCount and ParserParallelism
**Dashboard not updating**: Set UseHighThroughputMode=false
**Queue overflow**: Increase ChannelCapacity

## Performance

| Clients | Lines/sec | Memory | CPU |
|---------|-----------|--------|-----|
| 10 | 5K | 500MB | 10% |
| 50 | 20K | 1GB | 25% |
| 200 | 50K | 2GB | 50% |
| 500+ | 100K | 4GB | 80% |

## Best Practices

- Start with defaults
- ParserParallelism = 2x ConsumerCount
- Lower BatchTimeoutMs for real-time
- Use high throughput for events only

## Hardware

**Minimum**: 2 cores, 2GB RAM, SSD
**Recommended**: 4-8 cores, 4-8GB RAM, NVMe
**Large**: 16+ cores, 16GB+ RAM, NVMe RAID

## Monitor Script

```bash
#!/bin/bash
while true; do
  curl -s http://localhost:8080/api/performance/stats | jq
  sleep 5
done
```