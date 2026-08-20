# 裸机版 LANCache { #bare-metal }

[zeropingheroes/lancache-bare-metal](https://github.com/zeropingheroes/lancache-bare-metal) 直接在宿主机上运行缓存的 nginx（不使用 Docker）。它将按服务拆分的日志文件（`steam-access.log`、`blizzard-access.log`、`epicgames-access.log`、`riot-access.log`、`windows-update-access.log`、`fallback-access.log`）写入 `/srv/lancache/logs/http/`，使用不带服务标签的 `http-detailed` 格式。LANCache Manager 原生支持这种布局：挂载日志和缓存目录即可看到流量，无需改动 nginx 配置。

### 完整 Compose 示例

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    # 共享宿主机 PID 命名空间，管理器才能找到宿主机上的 nginx，
    # 并在删除或重写日志后要求它重新打开日志。
    pid: host
    # Docker 默认授予 CAP_KILL，镜像在切换到下面的非 root PUID 时会保留该能力。
    # 仅当部署使用 cap_drop 精简默认能力集时才需要取消注释。
    # cap_add:
    #   - KILL
    ports:
      - "8080:80"
    environment:
      - PUID=33          # 使用你常规的非 root ID，无需 root
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs
      - LanCache__CachePath=/cache
    volumes:
      - ./data:/data                       # 数据库、API 密钥、主题、预填充状态
      - /srv/lancache/logs/http:/logs:ro   # 裸机版按服务日志
      - /srv/lancache/data:/cache:ro       # 去掉 :ro 可启用清空缓存和删除游戏
```

使用 `docker run` 时，对应参数为 `--pid=host`，仅在使用了 `cap_drop` 后才需要 `--cap-add=KILL`。挂载上一级目录 `/srv/lancache/logs` 也可以，管理器会自动找到其中的 `http/` 文件夹。

**为什么需要 `pid: host`：** 删除操作后管理器会重写日志文件，nginx 必须重新打开日志，否则会继续写入已删除的 inode。有了宿主机 PID 可见性和 `CAP_KILL`，管理器就能自动向宿主机 nginx 发送信号。缺少它们时删除操作仍会完成，但日志重开会报告失败，需要你自行执行 `nginx -s reopen`。

**容器化 nginx 写裸机日志：** 如果 nginx 在容器中运行但写入按服务日志布局，请不要使用 `pid: host`，改为挂载 Docker 套接字（`/var/run/docker.sock:/var/run/docker.sock`）。

### 日志轮转

管理器不会截断或轮转裸机日志文件；定时任务只会要求 nginx 重新打开当前日志。请用宿主机的 `logrotate` 控制日志增长，并在规则中重新打开 nginx：

```text
postrotate
    nginx -s reopen
endscript
```

### 可用功能

日志级和磁盘级功能全部可用：

- **监控：** 实时活动、仪表盘、下载历史、客户端与服务统计、游戏识别（Steam depot、暴雪产品、Riot 域名）。
- **日志：** 按服务日志统计、日志删除、删除日志文件。
- **磁盘：** 按游戏和按服务删除缓存、重复未命中损坏扫描、逐出跟踪、清空整个缓存。每次删除文件前都会先核对文件自身嵌入的缓存键。

**关于命中率：** 暴雪与 Windows 更新按 1 MB 分片下载，而 nginx 只记录第一个分片的缓存状态，因此这两个服务的命中率是近似值（字节数始终精确）。标准容器版同样分片，这并非裸机特有。

### 备选方案：把裸机版切换到标准日志格式

如果你希望使用容器风格的合并日志，也可以修改裸机版 nginx 配置。在 `nginx.conf` 的 `http {}` 块中添加标准格式：

```nginx
log_format cachelog '[$cacheidentifier] $remote_addr / - - - [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent" "$upstream_cache_status" "$host" "$http_range"';
```

然后在 `caches-available/` 下的每个站点文件中设置服务名，并让所有站点指向同一个日志文件：

```nginx
set $cacheidentifier steam;   # 各站点分别为 blizzard / epicgames / riot / wsus
access_log /srv/lancache/logs/http/access.log cachelog;
```

执行 `sudo nginx -s reload`（或 `systemctl reload nginx`）重新加载，并按上文挂载 `/srv/lancache/logs/http`。**有一个警告，而且很重要。** 合并日志会让 LANCache Manager 把该环境识别为标准容器安装，但磁盘上的文件仍然带着裸机缓存键，因此移除游戏和损坏扫描会按并不存在的键去查找文件。只要旧的按服务日志仍与合并日志并存，LANCache Manager 看到的就是相互矛盾的证据，会直接拒绝这些功能。无论哪种情况，清空整个缓存都仍然可用。请勿在打过补丁的缓存上移除游戏或进行损坏扫描。

-----
