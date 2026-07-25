# Prometheus 指标 { #grafana--prometheus }

应用在 `/metrics` 上暴露 Prometheus 指标。可以抓取它们，在 Grafana 或任何兼容 Prometheus 的系统中构建仪表板，对缓存命中率设置告警——按你的需要使用。指标地址和刷新设置同样可以在**管理 → 集成**中查看。

### 可用指标

最常用的几个序列：

| 指标 | 描述 |
|--------|-------------|
| `lancache_cache_capacity_bytes` | 总存储容量 |
| `lancache_cache_used_bytes` | 当前已用空间 |
| `lancache_cache_free_bytes` | 剩余可用空间 |
| `lancache_cache_hit_bytes_total` | 节省的带宽（缓存命中） |
| `lancache_cache_miss_bytes_total` | 新下载的数据 |
| `lancache_cache_hit_ratio` | 缓存命中率（0-1） |
| `lancache_active_downloads` | 当前活跃下载数 |
| `lancache_active_clients` | 近期活跃客户端数 |
| `lancache_service_downloads_total` | 按服务统计的下载量 |
| `lancache_service_bytes_total` | 按服务统计的带宽 |
| `lancache_service_hit_ratio` | 按服务统计的命中率 |
| `lancache_client_bytes_total` | 按客户端统计的带宽 |

还有更多——吞吐量、按小时的分解统计、缓存增长趋势、距离写满的预计天数、高峰时段统计，以及按服务的命中/未命中序列。在浏览器中打开 `/metrics` 即可看到带说明文字的完整指标集。

### Prometheus 配置

```yaml
scrape_configs:
  - job_name: 'lancache-manager'
    static_configs:
      - targets: ['lancache-manager:80']
    scrape_interval: 30s
    metrics_path: /metrics
```

如果你设置了 `Security__RequireAuthForMetrics=true`，加上 Bearer 认证：

```yaml
    authorization:
      type: Bearer
      credentials: 'your-api-key-here'
```

### 查询示例

```promql
# 缓存命中率百分比
lancache_cache_hit_ratio * 100

# 过去 24 小时节省的带宽
increase(lancache_cache_hit_bytes_total[24h])

# 缓存使用量（GB）
lancache_cache_used_bytes / 1024 / 1024 / 1024
```
