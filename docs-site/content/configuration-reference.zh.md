# 配置参考 { #configuration }

本节是一份查询表。浏览标题，按需深入。真正涉及决策的两项设置各有专门的说明：[数据库模式](choosing-an-image-and-database-mode.md#image-variants)和[预填充网络](prefill.md#prefill-network)。

如果是常规安装，从代码仓库中的 [`docker-compose.yml`](https://github.com/regix1/lancache-manager/blob/main/docker-compose.yml) 开始即可。想一次看到全部支持的变量，跳转到本节末尾的[完整带注释 Compose 示例](#complete-compose)。

### 数据卷 { #volumes }

| 数据卷 | 用途 | 说明 |
|--------|------|------|
| `/data` | PostgreSQL 数据库、安全信息、状态与配置、主题、已缓存的图片 | 必需 |
| `/logs` | LANCache 访问日志 | 添加 `:ro` 可设为只读 |
| `/cache` | LANCache 缓存文件 | 添加 `:ro` 可只监控而不修改文件 |
| `/var/run/docker.sock` | Docker API 访问 | 可选。nginx 日志轮转以及 Steam/Epic/Battle.net/Riot/Xbox 预填充需要它 |

### 必需设置 { #required-settings }

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `PUID` | `33`（随附的 Compose 文件） | 应用运行所用的用户 ID。应与你的缓存和日志文件的所有者一致。 |
| `PGID` | `33`（随附的 Compose 文件） | 应用运行所用的组 ID。 |
| `TZ` | `UTC` | 日志时间戳所用的时区（例如 `America/Chicago`）。也接受 `TimeZone` 作为后备写法。 |
| `LanCache__LogPath` | - | 容器内 LANCache 访问日志的路径。 |
| `LanCache__CachePath` | - | 容器内 LANCache 缓存目录的路径。 |

**该用哪个 PUID/PGID？** 与你的缓存和日志文件所有者保持一致——用 `ls -n /path/to/cache` 就能看到。随附的 Compose 文件使用 `33:33`（www-data），适合大多数标准的 lancache 安装。Unraid 使用 `99:100`。如果运行原始镜像时没有设置 `PUID` 或 `PGID`，入口脚本会把缺少的那一个回退为 `1000`。请把它当作兜底值，而不是推荐值。

### PostgreSQL { #postgresql }

模式选择和完整的 Compose 示例见[选择镜像与数据库模式](choosing-an-image-and-database-mode.md#image-variants)。变量如下：

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `POSTGRES_MODE` | `embedded` | `embedded` 或 `external`。 |
| `POSTGRES_USER` | `lancache` | PostgreSQL 用户名。两种模式均适用。 |
| `POSTGRES_PASSWORD` | - | PostgreSQL 密码。内嵌模式下若未设置，UI 会显示设置页面；外部模式下必须设置（或在应用连接前通过 UI 后备表单输入）。 |
| `POSTGRES_HOST` | - | **仅外部模式。** Postgres 服务器的主机名或 IP。 |
| `POSTGRES_PORT` | `5432` | **仅外部模式。** |
| `POSTGRES_DB` | `lancache` | 数据库名称。两种模式均适用。 |

### 安全 { #security }

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `Security__EnableAuthentication` | `true` | 管理操作和 API 文档需要 API 密钥。仅在本地开发时关闭。 |
| `Security__GuestSessionDurationHours` | `6` | 默认访客会话时长（也可在 UI 中配置）。 |
| `Security__RequireAuthForMetrics` | `false` | `/metrics` 端点是否需要 API 密钥。管理 → 集成中的 UI 开关设置后会覆盖此值。 |
| `Security__AllowedOrigins` | （空） | 逗号分隔的 CORS 允许列表。为空则允许所有来源。 |
| `Security__ApiKeyPath` | `/data/security/api_key.txt` | 覆盖管理员 API 密钥的读写文件路径。当你从 `/data` 之外绑定挂载密钥时很有用。 |
| `Security__KnownProxyNetworks` | （空） | 用于 `X-Forwarded-For` 的可信代理网络 CIDR 列表，逗号分隔（例如 `172.16.0.0/12,10.0.0.0/8`）。当 nginx、Traefik 或其他反向代理位于管理器前面时设置此项，客户端 IP 才能被正确报告。回环地址始终受信任。如果代理位于另一台主机或另一个容器上而此项留空，登录限流会把所有客户端都算在代理的地址上，此时一个人反复输错密码就会把其他所有人锁在门外一分钟。 |
| `Security__TrustAllProxies` | `false` | 无条件信任每一个上游代理。方便本地开发使用。**切勿在暴露于公网的主机上启用**——任何人都能伪造客户端 IP。 |
| `Security__ForceSecureCookies` | `false` | 即使请求未被识别为 HTTPS，也强制在会话 Cookie 上加 `Secure` 标志。在 TLS 终止型反向代理后运行时启用。 |

并非每项 `/metrics` 设置都有对应的环境变量。指标更新间隔、示例配置中使用的 Prometheus 抓取间隔，以及按游戏统计序列导出的游戏数量，都在管理 → 集成中设置。参见 [Prometheus 指标](prometheus-metrics.md#grafana--prometheus)。

#### 访问级别

| 级别 | 可执行的操作 | 示例 |
|-------|----------------|----------|
| **管理员** | 全部操作。需要 API 密钥。 | 清除缓存、处理日志、更改设置 |
| **访客** | 只读视图。需要管理员认证或访客会话。 | 浏览下载、统计、事件、客户端数据 |

要在不分享 API 密钥的情况下让别人获得只读访问权限，请在**用户**页面确认访客登录处于**未锁定**状态，访客随后在登录界面点击**访客模式**即可。会话时长等其他默认值在**访客默认设置**中。每个页面和每项操作都需要管理员认证或一个访客会话，只有一个例外：除非你设置 `Security__RequireAuthForMetrics=true`，否则 `/metrics` 是公开的。

### 预填充设置 { #prefill-config }

预填充会为本表中几乎所有内容自动检测出合适的值。使用前需要了解三点：

- **把 `Prefill__LancacheIp` 设置为你缓存服务器的 IP，预填充就不再依赖 DNS。** 这一点对 Battle.net 尤为重要，它的 CDN 域名经常不在 lancache DNS 中，可能导致预填充挂起。
- 不设置的话，管理器会自动检测你的缓存。它会对几个候选地址做一次快速的健康检查，只会使用真正像 lancache 一样应答的地址。
- 只有在自动检测出错时才需要用到其他变量。决策表见[网络设置](prefill.md#prefill-network)。

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `Prefill__LancacheIp` | （未设置） | 你**缓存服务器**（保存缓存文件的 HTTP 服务器，端口 80）的 IP 或主机名。会作为 `LANCACHE_IP` 转发给守护进程；随后守护进程使用伪造的 `Host:` 头直接连接，跳过 CDN 流量的 DNS 查询。最可靠的覆盖项——只要你的 DNS 不是标准的 `lancache-dns`，就应该设置它。 |
| `Prefill__LancacheDnsIp` | `auto` | 你**DNS 服务器**（lancache-dns、AdGuard、Pi-hole——端口 53）的 IP。会写入预填充容器的 `/etc/resolv.conf`，让守护进程用它解析 CDN 主机名。仅在 `bridge` 模式下使用——Docker 会在 `host` 网络容器上静默丢弃 DNS 覆盖。`auto` 会复用检测到的 `lancache-dns` 容器的 IP。 |
| `Prefill__NetworkMode` | `auto` | 预填充容器的 Docker 网络模式。接受 `host`、`bridge` 或某个 Docker 网络名称。`auto` 会根据你的 `lancache-dns` 容器推断模式。 |
| `Prefill__SteamDockerImage` | `ghcr.io/regix1/steam-prefill-daemon:latest` | Steam 预填充容器所用的 Docker 镜像。 |
| `Prefill__EpicDockerImage` | `ghcr.io/regix1/epic-prefill-daemon:latest` | Epic 预填充容器所用的 Docker 镜像。 |
| `Prefill__BattlenetDockerImage` | `ghcr.io/regix1/battlenet-prefill-daemon:latest` | Battle.net 预填充容器所用的 Docker 镜像。 |
| `Prefill__RiotDockerImage` | `ghcr.io/regix1/riot-prefill-daemon:latest` | Riot 预填充容器所用的 Docker 镜像。 |
| `Prefill__XboxDockerImage` | `ghcr.io/regix1/xbox-prefill-daemon:latest` | Xbox 预填充容器所用的 Docker 镜像。 |
| `Prefill__SessionTimeoutMinutes` | `120` | 非持久管理员预填充会话的总生命周期。访客会话和持久会话使用各自独立的限制。 |
| `Prefill__StallTimeoutSeconds` | `180` | 高级设置。非持久会话被判定为停滞前的无进展时长。计划预填充使用自己独立的 30 分钟超时。 |
| `Prefill__DaemonBasePath` | `/data/prefill` | 存储预填充会话状态的容器内路径。 |
| `Prefill__HostDataPath` | `auto` | 映射到管理器 `/data` 数据卷的主机路径。从管理器的挂载配置中检测；仅在检测失败时（不常见的平台、自定义数据卷驱动）才需要显式设置。 |
| `Prefill__UseTcp` | `auto` | 使用 TCP 而非 Unix 域套接字与守护进程通信。`auto` 在 Windows 上解析为 `true`，在 Linux 上为 `false`。*Linux 用户只有在想强制使用 TCP 模式时才需要设置此项。* |
| `Prefill__TcpPort` | `45555` | 守护进程在其容器内监听的 TCP 端口。*仅用于 TCP 模式——Windows 默认如此，Linux 仅在 `Prefill__UseTcp=true` 时如此。* |
| `Prefill__HostTcpPort` | （随机空闲端口） | 守护进程容器在主机上发布的 TCP 端口。*仅 TCP 模式。* |
| `Prefill__TcpHost` | `127.0.0.1` | 守护进程绑定、管理器通过 TCP 连接的主机。*仅 TCP 模式。* |

!!! note
    **TCP 模式是平台分界线。** 在 Windows 上，预填充容器通过 TCP 通信，因为 Windows 不向 Docker 暴露 Unix 域套接字。在 Linux 上，预填充默认使用 Unix 域套接字——除非你设置 `Prefill__UseTcp=true`，否则上面四个 TCP 变量都会被忽略。标准的 Linux 安装可以完全跳过 TCP 相关的行。

### 路径与数据源 { #paths-and-datasources }

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `LanCache__EnvFilePath` | （自动） | lancache `.env` 文件的路径（用于读取 `CACHE_DISK_SIZE`）。未设置时会在常见位置搜索。 |
| `LanCache__AutoDiscoverDatasources` | `false` | 从 `/cache` 和 `/logs` 下匹配的子目录自动检测数据源，最多向下三层。 |

如果你运行多个缓存实例，或者把不同服务分散在多个驱动器上，请参见[多数据源](multiple-datasources.md)。

### Nginx 日志轮转 { #nginx-log-rotation }

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `NginxLogRotation__Enabled` | `true` | 通知 nginx 在应用轮转日志后重新打开日志文件。需要 Docker 套接字。 |
| `NginxLogRotation__ContainerName` | （空 = 自动检测） | LANCache 容器名称。留空（或设为 `auto`）时，应用会查找名称中包含 "lancache" 的容器。 |
| `NginxLogRotation__ScheduleHours` | `24` | 检查是否需要轮转的频率。 |

### API 与高级设置 { #api-and-advanced }

| 变量 | 默认值 | 描述 |
|----------|---------|-------------|
| `ApiOptions__MaxClientsPerRequest` | `1000` | 单次统计请求最多返回的客户端数量。 |
| `ApiOptions__DefaultClientsLimit` | `100` | 未指定限制时的默认客户端数量。 |
| `Optimizations__EnableGarbageCollectionManagement` | `false` | 在管理页面显示内存管理控件。适合低内存主机。 |
| `ASPNETCORE_URLS` | `http://+:80` | 内部端口绑定。除非你清楚为什么要改，否则不要改动。 |
| `ConnectionStrings__DefaultConnection` | （自动） | 完整的 PostgreSQL 连接字符串覆盖项。面向单个 `POSTGRES_*` 变量无法满足的复杂配置的高级用户。 |
| `CacheSnapshots__RetentionDays` | `90` | 缓存快照的保留时长。更早的快照会被自动删除。 |
| `CacheSnapshots__IntervalMinutes` | `60` | 高级设置。记录一次缓存大小快照的频率。 |

### 完整带注释的 Compose 示例 { #complete-compose }

想要一份列出全部内容的文件？下面这个例子是一份完整、可直接使用的 Compose 文件。生效的几行与快速开始一致；每一项可选设置都已列出但被注释掉，并注明了默认值和适用场景，因此可以放心复制使用。

<details markdown>
<summary><strong>完整带注释的 Compose 示例</strong>——全部支持的变量</summary>

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8080:80"
    volumes:
      - ./data:/data                                # 数据库、API 密钥、主题、预填充状态
      - /mnt/lancache/logs:/logs:ro                 # LANCache 访问日志
      - /mnt/lancache/cache:/cache:ro               # 去掉 :ro 可允许清除缓存和移除游戏
      - /var/run/docker.sock:/var/run/docker.sock   # 可选：预填充和 nginx 日志轮转需要
    environment:
      # --- 必需（与快速开始相同） ---
      - PUID=33                                 # 应用运行所用的用户 ID；33 = 随附 Compose 文件的值（www-data）。Unraid：99
      - PGID=33                                 # 组 ID；33 = 随附 Compose 文件的值。Unraid：100
      - TZ=America/Chicago                      # IANA 时区；默认 UTC
      - LanCache__LogPath=/logs/access.log      # 容器内的访问日志
      - LanCache__CachePath=/cache              # 容器内的缓存目录

      # --- PostgreSQL（默认以内嵌 Postgres 运行，无需额外设置） ---
      # - POSTGRES_MODE=embedded                # embedded（默认）或 external；精简镜像仅支持 external
      # - POSTGRES_USER=lancache                # 默认 lancache
      # - POSTGRES_PASSWORD=                    # 密钥；留空则首次运行页面会要求输入
      # 仅外部模式：
      # - POSTGRES_HOST=lancache-db
      # - POSTGRES_PORT=5432
      # - POSTGRES_DB=lancache

      # --- 安全 ---
      # - Security__EnableAuthentication=true     # false 会关闭全部认证；仅限本地开发
      # - Security__RequireAuthForMetrics=false   # true = /metrics 需要 Bearer 令牌
      # - Security__GuestSessionDurationHours=6
      # - Security__AllowedOrigins=               # CORS 来源列表，逗号分隔；为空表示全部允许
      # - Security__ForceSecureCookies=false      # 在 TLS 终止型代理之后运行时设为 true
      # - Security__KnownProxyNetworks=           # 可信代理的 CIDR 列表，逗号分隔，例如 172.16.0.0/12
      # - Security__TrustAllProxies=false         # 暴露于公网的主机上永远不要设为 true
      # - Security__ApiKeyPath=/data/security/api_key.txt

      # --- 预填充（自动检测；仅在检测失败时才需要设置这些） ---
      # - Prefill__LancacheIp=192.168.1.10        # 缓存服务器 IP；最可靠的覆盖项
      # - Prefill__NetworkMode=auto               # host、bridge、某个 Docker 网络名称，或 auto
      # - Prefill__LancacheDnsIp=auto             # DNS 服务器 IP；仅 bridge 模式
      # - Prefill__SteamDockerImage=ghcr.io/regix1/steam-prefill-daemon:latest
      # - Prefill__EpicDockerImage=ghcr.io/regix1/epic-prefill-daemon:latest
      # - Prefill__BattlenetDockerImage=ghcr.io/regix1/battlenet-prefill-daemon:latest
      # - Prefill__RiotDockerImage=ghcr.io/regix1/riot-prefill-daemon:latest
      # - Prefill__XboxDockerImage=ghcr.io/regix1/xbox-prefill-daemon:latest
      # - Prefill__SessionTimeoutMinutes=120      # 非持久管理员会话的生命周期
      # - Prefill__StallTimeoutSeconds=180        # 高级设置：非持久会话的停滞超时
      # - Prefill__DaemonBasePath=/data/prefill   # 必须位于 /data 之下
      # - Prefill__HostDataPath=auto              # /data 对应的主机路径；仅检测失败时设置
      # - Prefill__UseTcp=auto                    # auto = Windows 用 TCP，Linux 用 Unix 套接字
      # - Prefill__TcpPort=45555                  # 仅 TCP 模式
      # - Prefill__HostTcpPort=                   # 仅 TCP 模式；留空会随机选择一个空闲端口
      # - Prefill__TcpHost=127.0.0.1              # 仅 TCP 模式

      # --- Nginx 日志轮转（需要挂载 docker.sock） ---
      # - NginxLogRotation__Enabled=true
      # - NginxLogRotation__ContainerName=        # 留空或设为 "auto" 会查找 "lancache" 容器
      # - NginxLogRotation__ScheduleHours=24

      # --- API、优化、缓存快照 ---
      # - ApiOptions__MaxClientsPerRequest=1000
      # - ApiOptions__DefaultClientsLimit=100
      # - Optimizations__EnableGarbageCollectionManagement=false   # 仅低内存主机需要
      # - CacheSnapshots__RetentionDays=90        # 缓存大小历史保留时长
      # - CacheSnapshots__IntervalMinutes=60      # 高级设置：记录快照的频率
      # - ASPNETCORE_URLS=http://+:80             # 内部绑定；保持默认即可

      # --- 路径与数据源 ---
      # - LanCache__EnvFilePath=/lancache/.env    # 未设置 = 自动搜索常见位置
      # - LanCache__AutoDiscoverDatasources=false # 扫描 /cache 和 /logs 下的匹配子目录，最多三层
      # 多数据源会替代上面的 LogPath/CachePath。保持编号连续，
      # 并按同样的方式添加更多：__2__、__3__……
      # - LanCache__DataSources__0__Name=Default
      # - LanCache__DataSources__0__CachePath=/cache
      # - LanCache__DataSources__0__LogPath=/logs
      # - LanCache__DataSources__0__Enabled=true
      # - LanCache__DataSources__1__Name=Steam
      # - LanCache__DataSources__1__CachePath=/steam-cache
      # - LanCache__DataSources__1__LogPath=/steam-logs
      # - LanCache__DataSources__1__Enabled=true

      # --- 高级用户 ---
      # - ConnectionStrings__DefaultConnection=Host=/var/run/postgresql;Database=lancache;Username=lancache;Maximum Pool Size=20;Minimum Pool Size=2   # 覆盖 POSTGRES_*；若内嵌了密码则属于密钥
      # - Logging__LogLevel__LancacheManager.Infrastructure.Platform=Debug   # 任意日志分类；取值 Trace..None
```

</details>

**从 1.10.3 升级？** 新增了两个变量：`Prefill__XboxDockerImage`（Xbox 成为第五个预填充平台）和高级选项 `Prefill__StallTimeoutSeconds`。没有变量被重命名或移除，所以现有的 Compose 文件可以照常使用。一处清理：`Security__MaxAdminDevices` 是一个旧的无效设置，当前代码不会读取它——可以删除。

-----
