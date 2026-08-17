# 故障排除 { #troubleshooting }

在查看下面的条目之前，先打开**管理 → 状态检查**。它会测试 DNS、缓存可达性，以及近期下载是否用到了缓存，通常能直接指出问题所在。

### 日志没有被处理

1. 在**管理 → 日志与缓存 → 日志处理**中检查日志路径。
2. 确认你的数据卷挂载与 `LanCache__LogPath` 一致。
3. 在同一页面的 `⋯` 菜单中选择**全部处理**。
4. 查看容器日志：`docker logs lancache-manager`。

### Web 界面显示"无日志文件"（但文件确实存在）

`LanCache__LogPath` 必须是**容器内访问日志文件的完整路径**，需要包含文件名——例如 `/logs/access.log`，而不只是 `/logs`。请确认：

1. 你的数据卷已把宿主机的日志目录挂载进容器（例如 `- /host/path/lancache/logs:/logs`）。
2. `LanCache__LogPath` 指向该*容器内*路径下的文件（`/logs/access.log`）。
3. 该文件在容器内可读：`docker exec -it lancache-manager cat /logs/access.log | head`。

网络共享（NFS/SMB）没有问题。`ls -la` 输出末尾的 `+`（例如 `-rw-r--r--+`）只是 NFS 的 ACL 标记——它**不会**阻止读取。如果 `cat` 能打印出文件内容，那问题就不在权限，应该检查的是所配置的路径。

### 仪表板没有显示缓存大小 / 磁盘占用

`LanCache__CachePath` 必须指向**直接包含哈希缓存文件夹**（名称形如 `00`、`1a`、`ff`）的目录，而不是它的上一级。

标准的 lancache（monolithic）布局会把内容嵌套在下一层。如果你把缓存数据目录挂载到 `/cache`，实际的哈希文件夹通常位于 `/cache/cache`——这时应设置 `LanCache__CachePath=/cache/cache`。如果指向的层级过高，仪表板会显示这个驱动器，但报告的缓存用量为零。网络挂载（NFS/SMB）的缓存行为相同。

确认该路径下实际存在什么：

```bash
docker exec -it lancache-manager ls /cache
# 你应该看到哈希文件夹（00、1a……ff），而不是单个 "cache" 文件夹
```

### 游戏没有被识别

**Steam：**

1. 刷新映射：进入**管理 → 计划任务**，找到 **Steam 游戏映射** 那一行，点击**立即运行**（行尾的播放图标）。
2. 为你关心的任何私有 depot 添加自定义映射。
3. 重新导入历史记录：在**日志与缓存**中，从**日志处理**卡片的 `⋯` 菜单选择**重新定位**，把读取位置移回起点，再次处理。已处理过的条目会自动跳过，所以重新处理是安全的。

**Epic：**

1. 在**管理 → 集成**中登录 Epic。
2. 进入**管理 → 计划任务**，找到 **Epic 游戏映射** 那一行，点击**立即运行**。映射服务会查询 Epic API，识别你缓存中的内容。
3. 游戏名称和封面图会自动下载。

### 丢失 API 密钥

```bash
# 如果 /data 是绑定挂载（默认的 docker-compose.yml 就是这样），可以直接在主机上读取
cat ./data/security/api_key.txt

# 在任何地方都可用：直接从容器内部读取（命名卷同样适用）
docker exec lancache-manager cat /data/security/api_key.txt
```

在 Windows 的 Git Bash 中，请在 `docker exec` 命令前加上 `MSYS_NO_PATHCONV=1`（即 `MSYS_NO_PATHCONV=1 docker exec lancache-manager cat /data/security/api_key.txt`），否则路径会被改写成本地 Windows 路径。

之后的容器重启只打印文件路径和一段短提示，不会打印完整密钥。完整密钥仅在首次创建或轮换后写入日志。

要轮换密钥，停止容器，删除 `./data/security/api_key.txt`，然后重新启动。

### 权限问题

确保 `PUID` 和 `PGID` 与你的缓存和日志文件所有者一致：

```bash
ls -n /path/to/cache
```

### 预填充无法运行

五个平台的排查清单是一样的。按顺序检查：

1. 确认 Docker 套接字已挂载。
2. 确认你已在 lancache-manager 中以管理员身份完成认证。
3. 查看容器日志中的网络诊断块（`═══ PREFILL CONTAINER NETWORK DIAGNOSTICS ═══`）——它会告诉你容器是否有网络，以及 CDN 域名解析到了哪里。
4. **预填充容器内没有互联网。** 容器无法连接到平台服务器。常见修复方法：
   - 设置 `Prefill__NetworkMode=bridge`（适用于大多数环境）。
   - 确认你的 Docker 网络有出站互联网。
   - 检查防火墙的出站流量规则。
5. **下载过程中出现 HTTP 400 错误。** 容器无法把 CDN 域名解析到你的缓存。最可靠的修复是设置 `Prefill__LancacheIp=<你的缓存 IP>`——它会为 CDN 流量完全绕开 DNS。完整决策表（以及 `LancacheDnsIp`/`NetworkMode` 各自的作用）见[网络设置](prefill.md#prefill-network)。
6. **IPv6 流量绕过了 DNS。** 如果你的网络启用了 IPv6，查询可能会绕过 `lancache-dns`。应用已经在预填充容器中禁用了 IPv6 以防止这种情况。
7. **Epic OAuth 始终无法连接。** 在弹出的浏览器窗口中完成 OAuth 流程。令牌会被安全存储，并在会话之间保持有效。

查找 `lancache-dns` 的 IP：

```bash
docker inspect lancache-dns | grep IPAddress
```

### 计划预填充"跳过"了某些服务

这是[计划预填充](prefill.md#scheduled-prefill)的正常设计行为：计划运行只会使用已经在运行、且对 Steam/Epic/Xbox 而言已登录的持久容器。从计划预填充卡片启动该容器，如有需要就登录；下一次运行就会包含它。跳过不算失败，计划仍会照常继续。

### 预填充后命中率看起来比预期低

通常没有问题——这个数字把预填充本身也算了进去。仪表板的命中率是按字节加权统计所有接触过缓存的客户端得出的，包括预填充容器本身（[Prometheus](prometheus-metrics.md) 的 `lancache_service_hit_ratio` 指标也是同样的混合方式）。

在空缓存上，一次预填充运行接近 100% 未命中，随后的安装接近 100% 命中。两者合起来，单个游戏大致会拉平到 50% 左右。每次重复安装都会把这个数字推向 66%、75% 甚至更高，因为未命中的字节数是固定的基数，而每次安装都会给总数增加更多命中字节。

想把预填充流量从视图中隐藏，可以在下载/仪表板的客户端筛选器中过滤掉客户端 `127.0.0.1`。守护进程运行在同一台主机上，这样就能隐藏它的流量而不影响底层数据。**管理 → 客户端**中的客户端排除更进一步：在那里被排除的客户端也会被排除在 [`/metrics`](prometheus-metrics.md) 之外。

想查看某次具体安装的真实命中率，可以把下载页切换到**复古**视图，打开设置面板，让**显示**下的**按游戏分组**保持未勾选，再查看该客户端自己的那一行，或者在 `access.log` 中按其 IP 进行 grep。管理器只统计 nginx `upstream_cache_status` 中字面意义上的 `HIT`/`MISS`，因此结果始终与手动统计日志的结果一致。

即使是一次干净的安装，出现一些真实的未命中也是正常的：

- 预填充守护进程默认只抓取 Windows/x64/英语的 depot
- 之前一次没有使用 `--force` 而被中断的运行会留下缺口
- 绕过 `lancache-dns` 的 HTTPS/IPv6 流量永远不会经过缓存

### 调试日志

在路径自动检测失败、预填充容器无法启动、数据卷挂载看起来不对，或者你使用的是不常见的平台时，可以开启详细的平台日志：

```yaml
environment:
  - Logging__LogLevel__LancacheManager.Infrastructure.Platform=Debug
```

你会得到以下方面的额外细节：

- 路径解析（容器路径 vs 主机路径）
- 文件系统操作与权限检查
- Docker 套接字通信与容器检测
- 数据卷挂载检测
- Linux/Windows 平台差异

使用方法：添加该变量，用 `docker compose up -d` 重启，复现问题，然后查看 `docker logs lancache-manager`。排查完成后移除该变量——它的日志量很大。

-----
