# 常见场景 { #recipes }

### Unraid { #unraid }

代码仓库中提供了一个 Docker 模板：[`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml)。把它保存到 Unraid 主机的 `/boot/config/plugins/dockerMan/templates-user/` 目录下，或者把原始文件的 URL 粘贴到 **Docker → 添加容器 → 模板**中。然后按照与 Compose 示例相同的方式填写路径和变量。在 Unraid 上使用 `PUID=99` / `PGID=100`（也就是 `nobody:users` 的默认值）。

### 多数据源 { #multiple-datasources }

大多数人只运行单个 LANCache 实例，永远用不到这一节。只有当服务分散在不同的缓存目录，或者需要把多台 LANCache 服务器合并到一个仪表板中时，才需要它。

"数据源"是一对成组的日志 + 缓存目录。每个数据源被单独处理和跟踪，然后在仪表板和下载视图中汇总。

常见的使用场景：

- **服务外包到独立存储**——Steam 与其他服务位于不同的驱动器上。
- **多个 LANCache 实例**——为不同房间或不同用途分别部署缓存服务器。
- **分区存储**——不同服务位于不同的分区。

#### 自动发现（推荐）

把应用指向父目录，让它自己扫描：

```yaml
environment:
  - LanCache__LogPath=/logs
  - LanCache__CachePath=/cache
  - LanCache__AutoDiscoverDatasources=true
```

自动发现会把你的缓存路径和日志路径成对地逐层遍历，最深到根目录下第三层。该深度是固定的，不可配置。只要某一层的缓存文件夹和日志文件夹都有实际内容，这一层就会成为一个数据源；发现一个数据源之后，仍会继续搜索它的内部：

1. **根目录**——如果 `/logs/access.log` 存在，且 `/cache` 中包含 LANCache 的哈希目录（`00/`、`01/` 等），根目录会成为 "Default"。
2. **嵌套文件夹**——第 1 到第 3 层中任何匹配成功的缓存/日志文件夹对，都会创建一个以该缓存文件夹命名的数据源（例如 `/cache/steam` + `/logs/steam` → "Steam"）。
3. **第 4 层及更深处永远不会被扫描**——请把文件夹上移一层，或改用手动配置。

匹配规则：

- **名称匹配**先精确匹配，再忽略大小写，最后做归一化匹配（忽略短横线、下划线以及末尾的 "s"）。
- **命名不同的中间包装文件夹不会阻断发现。** 如果同一层的缓存文件夹和日志文件夹名称不一致，但两侧都恰好只有一个子文件夹，这一对仍会被匹配上。而两个各自已经是有效数据源的文件夹，永远不会被互相配对。
- **会被跳过、但不影响扫描继续进行的：** 隐藏和系统文件夹、LANCache 的两字符哈希桶、符号链接，以及无法读取的分支。
- **与已发现的数据源重名的名称会被跳过并记录日志**，而不是悄悄覆盖前一个。
- **如果任何地方都没有发现有效结构，** 应用会回退到使用你配置的路径构建单个 `default` 数据源。

带分组父目录的示例布局，仍然只创建三个数据源（Default、Steam、Epic）：

```
/mnt/lancache/
├── cache/
│   ├── 00/, 01/, a1/, ff/       ← 默认缓存（哈希目录在根级，第 0 层）
│   └── outsourced/
│       ├── steam/
│       │   └── 00/, 01/, ...    ← Steam，第 2 层
│       └── epic/
│           └── 00/, 01/, ...    ← Epic，第 2 层
└── logs/
    ├── access.log               ← 默认日志
    └── outsourced/
        ├── steam/
        │   └── access.log       ← Steam 日志
        └── epic/
            └── access.log       ← Epic 日志
```

如果一个缓存文件夹在同一层级下没有对应的日志文件夹（反之亦然），会被静默跳过。它不会成为数据源，也不会报错。如果驱动器或目录结构过于不对称，自动发现无法正确配对，请改用下面的手动配置显式声明数据源。

#### 手动配置

如果驱动器完全位于不同位置，或者需要更精细的控制，可以显式声明每个数据源。若两者都设置了，手动配置优先于自动发现。

```yaml
environment:
  # 主 LANCache
  - LanCache__DataSources__0__Name=Default
  - LanCache__DataSources__0__CachePath=/cache
  - LanCache__DataSources__0__LogPath=/logs
  - LanCache__DataSources__0__Enabled=true

  # 独立驱动器上的 Steam
  - LanCache__DataSources__1__Name=Steam
  - LanCache__DataSources__1__CachePath=/steam-cache
  - LanCache__DataSources__1__LogPath=/steam-logs
  - LanCache__DataSources__1__Enabled=true
```

配合相应的数据卷挂载：

```yaml
volumes:
  - /mnt/lancache/cache:/cache:ro
  - /mnt/lancache/logs:/logs:ro
  - /mnt/steam-drive/cache:/steam-cache:ro
  - /mnt/steam-drive/logs:/steam-logs:ro
```

### 反向代理（Nginx） { #nginx-reverse-proxy }

LANCache Manager 可以在 nginx 后面正常运行。建议使用 HTTPS，如果计划跨域使用访客会话则是必需的（跨域图片 Cookie 需要 `Secure`）。

!!! tip
    在管理器前面架设了代理？记得同时设置 `Security__KnownProxyNetworks`（见[安全](configuration-reference.md#security)），这样客户端 IP 才能被正确报告。

#### 单一来源（推荐）

从同一个主机名同时提供 UI 和 API。Cookie 保持第一方，CORS 也就不成问题。

```nginx
server {
  listen 443 ssl http2;
  server_name lancache.example.com;

  ssl_certificate     /etc/letsencrypt/live/lancache.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/lancache.example.com/privkey.pem;

  # 如果响应体较大可以调高此值
  client_max_body_size 50m;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;

    # SignalR（WebSocket）
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 600s;  # 保持在 600 秒或以上，避免空闲的 SignalR WebSocket 连接被断开
  }
}

server {
  listen 80;
  server_name lancache.example.com;
  return 301 https://$host$request_uri;
}
```

#### 分离的 API 来源（仅在必要时使用）

如果 UI 和 API 位于不同的主机名：

- 用 `VITE_API_URL=https://api.lancache.example.com` 构建 UI。
- 保留 `SameSite=None; Secure` Cookie（应用已经这样设置）。
- 在 CORS 中为 UI 来源允许携带凭据。

```nginx
server {
  listen 443 ssl http2;
  server_name api.lancache.example.com;

  ssl_certificate     /etc/letsencrypt/live/api.lancache.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/api.lancache.example.com/privkey.pem;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;

    # SignalR（WebSocket）
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 600s;  # 保持在 600 秒或以上，避免空闲的 SignalR WebSocket 连接被断开
  }
}
```

### Prometheus 指标 { #grafana--prometheus }

应用在 `/metrics` 上暴露 Prometheus 指标。可以抓取它们，在 Grafana 或任何兼容 Prometheus 的系统中构建仪表板，对缓存命中率设置告警——按你的需要使用。指标地址和刷新设置同样可以在**管理 → 集成**中查看。

#### 可用指标

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

#### Prometheus 配置

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

#### 查询示例

```promql
# 缓存命中率百分比
lancache_cache_hit_ratio * 100

# 过去 24 小时节省的带宽
increase(lancache_cache_hit_bytes_total[24h])

# 缓存使用量（GB）
lancache_cache_used_bytes / 1024 / 1024 / 1024
```

-----
