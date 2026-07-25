# 常见场景 { #recipes }

Unraid 专用配置。其他内容请看对应指南：

- [多数据源](multiple-datasources.md) — 拆分缓存或合并多台 LANCache
- [反向代理](reverse-proxy.md) — 在管理器前使用 Nginx
- [Prometheus 指标](prometheus-metrics.md) — 抓取 `/metrics` 与示例查询

## Unraid { #unraid }

仓库提供了 Docker 模板：[`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml)。把它保存到 Unraid 上的 `/boot/config/plugins/dockerMan/templates-user/`，或把原始文件 URL 粘贴到 **Docker → Add Container → Template**。然后按 Compose 示例填写相同的路径和变量。在 Unraid 上请使用 `PUID=99` / `PGID=100`（默认的 `nobody:users`）。
