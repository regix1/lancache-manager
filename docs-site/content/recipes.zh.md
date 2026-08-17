# Unraid / 常见场景 { #recipes }

Unraid 专用配置。其他内容请看对应指南：

- [多数据源](multiple-datasources.md) — 拆分缓存或合并多台 LANCache
- [反向代理](reverse-proxy.md) — 在管理器前使用 Nginx
- [Prometheus 指标](prometheus-metrics.md) — 抓取 `/metrics` 与示例查询

## Unraid { #unraid }

仓库提供了 Docker 模板：[`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml)。它与 [`docker-compose.yml`](https://github.com/regix1/lancache-manager/blob/main/docker-compose.yml) 使用相同的路径和环境变量，并采用 Unraid 默认值 `PUID=99` / `PGID=100`（`nobody:users`）。

### 安装模板

1. 将 [`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml) 保存到 Unraid 上的 `/boot/config/plugins/dockerMan/templates-user/`，**或**把[原始文件 URL](https://raw.githubusercontent.com/regix1/lancache-manager/main/unraid/lancache-manager.xml) 粘贴到 **Docker → Add Container → Template**。
2. 将 **LANCache Logs** 和 **LANCache Cache** 指向你的 LANCache 共享路径。
3. 若需要 nginx 日志 reopen，以及 Steam / Epic / Battle.net / Riot / Xbox 预填充，请保留 Docker socket 挂载。
4. 在 **Show more settings** 中，当自动检测不可靠时设置 **Prefill Cache Server IP** 为 LANCache 的 HTTP IP（Battle.net 建议设置）。
5. 应用并打开 WebUI。用下面命令获取 API 密钥：

```bash
docker exec lancache-manager cat /data/security/api_key.txt
```

首次启动时，密钥也会打印在容器日志中。之后的重启只打印提示，请使用该文件。

### 说明

- 必需路径与变量与 Compose 快速开始一致：`/data`、`/logs`、`/cache`、`TZ`、`LanCache__LogPath`、`LanCache__CachePath`。
- 高级设置涵盖 PostgreSQL 模式、安全选项、五个预填充守护进程镜像、停滞超时、nginx 轮转计划，以及反向代理辅助项（`Security__KnownProxyNetworks`、`Security__ForceSecureCookies`）。
- 主机直接运行的 nginx（裸机）需要 Extra Parameters `--pid=host`，以便管理器在删除后请求 nginx reopen 日志。不需要 root（`PUID=0`）。
- 完整变量说明见[配置参考](configuration-reference.md)。
