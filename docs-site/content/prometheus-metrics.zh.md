# Prometheus 指标 { #grafana--prometheus }

应用在 `/metrics` 上暴露 Prometheus 指标。可以抓取它们，在 Grafana 或任何兼容 Prometheus 的系统中构建仪表板，对缓存命中率设置告警——按你的需要使用。指标地址和刷新设置同样可以在**管理 → 集成**中查看。

!!! note "预填充流量同样会被统计，与仪表板一致"

    预填充守护进程和其他客户端一样通过缓存下载，因此它的流量会记录在自己的客户端 IP 下，通常是 `127.0.0.1`。`/metrics` 和仪表板都会统计这部分流量，两者的数字一致。

    一次预填充运行几乎全是未命中字节，因此会在一段时间内拉低所有命中率序列。[故障排除](troubleshooting.md#troubleshooting)中说明了其中的算法，以及如何在视图中过滤掉该守护进程。

!!! warning "客户端排除现在同样作用于 `/metrics`"

    在**管理 → 客户端**中被排除的客户端，现在不仅会从应用界面中移除，也会从 `/metrics` 中移除。它不再出现在 `lancache_client_bytes_total` 以及其余 `lancache_client_*` 序列中，其字节数也不再计入 `lancache_service_*` 系列和全局总计。两种模式都会生效：**仅统计**让客户端仍显示在下载和实时速度中，但不计入总计和命中率；**隐藏**则将其从所有位置移除。

    如果你排除了任何客户端，相关序列会在包含此更改的版本上出现下降。历史数据不会被改写，但从该版本起的每个值都会低于此前的对应值。请重新检查所有依据旧数值调整过的 `rate()` 面板和告警阈值。

    这也是把预填充流量挡在 Prometheus 之外的方法：在**管理 → 客户端**中排除该守护进程的客户端 IP，通常是 `127.0.0.1`。

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

### 按游戏统计的指标

| 指标 | 描述 |
|--------|-------------|
| `lancache_game_bytes` | 该游戏累计提供的字节数 |
| `lancache_game_cache_hit_bytes` | 从缓存提供的字节数 |
| `lancache_game_cache_miss_bytes` | 从上游获取的字节数 |
| `lancache_game_downloads` | 下载次数 |
| `lancache_game_cache_hit_ratio` | 该游戏的命中率（0-1） |
| `lancache_game_active_downloads` | 当前活跃或最近 5 分钟内结束的下载数 |

这六个指标都带有相同的三个标签：`service`、`game` 和 `app_id`。`service` 的值为小写，与 `lancache_service_bytes_total` 已经使用的值一致，因此两组指标可以关联。注意 Xbox 流量在这里按其原始服务名分别出现，而应用自己的界面会把它们合并显示。

`app_id` 对 Steam 游戏是 Steam 应用 ID，对 Epic 游戏是 Epic 应用 ID，对暴雪、Riot 和 Xbox 这类按名称识别的游戏则是 `0`，与缓存检测为它们存储的值相同。它的作用是：即使游戏在目录中被改名，序列仍然保持同一身份。

只有按累计字节数排名靠前的游戏会被导出，默认 50 个，可以在**管理 → 集成 → 刷新和抓取速率**中修改上限。跌出前 N 名的游戏会直接停止导出，而不是上报 0，因此它的曲线会干净地结束，而不会掉到底部。修改上限会在下一个刷新周期生效，所以最多需要等待一个完整的数据刷新间隔。

这六个指标统计缓存记录过的所有下载，包括缓存文件此后已被驱逐的下载，这与本页其他下载类指标的口径一致。

### 磁盘上的游戏

| 指标 | 描述 |
|--------|-------------|
| `lancache_game_cache_bytes` | 该游戏在磁盘上占用的字节数（未去重） |
| `lancache_game_cache_files` | 该游戏找到的缓存文件数 |
| `lancache_games_on_disk_bytes` | 归属于游戏的总字节数，已去重 |
| `lancache_games_on_disk_count` | 磁盘上找到的游戏数量 |
| `lancache_identified_service_bytes` | 归属于某个服务但未归属到任何游戏的总字节数，已去重 |
| `lancache_detection_computed_timestamp` | 磁盘数据最近一次计算的时间，Unix 时间戳 |

磁盘类指标会跳过已驱逐的游戏，与"磁盘上的游戏"视图的做法一致，因此两者的数字相符。之所以提供 `lancache_detection_computed_timestamp`，是因为这些数字只在缓存扫描或移除操作时才会重新计算。没有它的话，一条水平的曲线既可能表示确实没有变化，也可能表示根本没有运行过计算。

!!! note "按游戏统计的磁盘字节数加起来不等于总量"

    `lancache_games_on_disk_bytes` 在所有游戏和所有服务之间做了去重，因为 lancache 会在不同游戏之间共享缓存对象。而按游戏统计的 `lancache_game_cache_bytes` 是原始值，共享对象会在每个用到它的游戏中各计一次。

    因此把按游戏的序列相加得到的数字会大于总量。这是预期行为而非缺陷，应用自己的仪表板也是把这两个数字并排显示的。

### Steam 映射覆盖率

| 指标 | 描述 |
|--------|-------------|
| `lancache_steam_unknown_game_bytes` | 未能识别出游戏名称的 Steam 下载所提供的字节数 |
| `lancache_steam_unknown_game_downloads` | 未能识别出游戏名称的 Steam 下载次数 |

这两个指标刻意只针对 Steam。WSUS 和裸机流量按设计本来就不带游戏名称，如果统计所有无名称的下载，会把一个完全正常的部署报告成有问题。

### 局域网活动

| 指标 | 描述 |
|--------|-------------|
| `lancache_event_bytes` | 标记到该活动的下载所提供的字节数 |
| `lancache_event_cache_hit_bytes` | 该活动的缓存命中字节数 |
| `lancache_event_downloads` | 该活动的下载会话数 |
| `lancache_event_cache_hit_ratio` | 该活动的命中率（0-1） |

这四个指标都带有 `event`（显示名称）和 `event_id`。它们只统计已标记的下载，与仪表板活动筛选使用的集合相同，并且和其他 `/metrics` 指标一样遵守客户端排除规则。

只导出按字节数排名前 50 的活动。没有已标记下载的活动会被省略，而不是上报 0。它们都是 gauge：请直接绘制。仪表板上按活动开始时间对齐的叠加曲线不会从这里导出——Prometheus 抓取的是累计值，而不是从每个活动开始起按小时的序列。

### 哪些指标可以配合 rate() 使用

以 `_total` 结尾的名称按惯例表示计数器，而 `rate()`、`irate()` 和 `increase()` 都是为计数器设计的。**上面这些新指标没有一个带 `_total`，也没有一个适合配合 `rate()` 使用。**

它们全都是 gauge 类型，并且数值确实可能下降：当某个服务不再出现在日志文件中时，旧的下载记录会被清理；驱逐也会把游戏从磁盘上移除。`rate()` 会把任何下降当成计数器重置，从而制造出一个根本没有发生过的巨大尖峰。

请直接绘制它们的曲线，如果需要变化速率，可以使用 `delta()` 或 `deriv()`。`avg_over_time()`、`max_over_time()`、`topk()` 和 `sum()` 都可以正常使用。

这确实让新名称与 `lancache_service_bytes_total` 及其同类不一致，后者同样是 gauge 类型却带着 `_total` 后缀。那些名称保持不变，因为改名会破坏所有已经在使用它们的仪表板。新名称按其真实类型命名，而不是延续旧有的这个错误。

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

# 带宽占用最高的 10 个游戏
topk(10, lancache_game_bytes)

# 按游戏统计的命中率百分比
lancache_game_cache_hit_ratio * 100

# 未能匹配到游戏的 Steam 字节数占比
lancache_steam_unknown_game_bytes / lancache_service_bytes_total{service="steam"}

# 每个局域网活动提供的字节数
topk(8, lancache_event_bytes)
```

最后这条查询两边的后缀不一致，这不是笔误。`lancache_steam_unknown_game_bytes` 是新增的 gauge 之一，不带 `_total`；`lancache_service_bytes_total{service="steam"}` 是已有序列，保留了它发布时的后缀。两边都是统计字节数的 gauge，所以这个比值本身是正确的。
