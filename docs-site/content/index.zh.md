# LANCache Manager

[LANCache](https://lancache.net/) 的 Web 界面。无需接触终端，即可查看缓存状态并进行操作。

仪表板提供实时视图：节省的带宽、缓存命中率、主要客户端以及各服务的分析数据。其后是完整的缓存浏览器，包含每个已缓存游戏的封面、大小和各客户端的下载历史。Prometheus 指标开箱即用，其中包括按游戏统计的带宽和命中率。

它也能直接操作缓存。在客人到达之前手动或按计划预填充 Steam、Epic、Battle.net、Riot 和 Xbox 游戏，并在浏览器中管理服务器：处理日志、清理缓存、检测损坏，以及在出现异常时检查 DNS 与缓存健康状况。

!!! important "请始终拉取 `latest` 标签"

    GitHub 的软件包页面会突出显示 `:dev`，因为开发版构建发布更频繁，但 `:dev` 仅供测试使用，随时可能出问题。

    ```bash
    docker pull ghcr.io/regix1/lancache-manager:latest
    ```

## 从这里开始

| 你想要 | 阅读 |
|---|---|
| 快速运行起来 | [快速开始](quick-start.md) |
| 从旧版本升级 | [升级](upgrading.md) |
| 了解每个页面的功能 | [功能一览](what-you-get.md) |
| 在活动前预填充游戏 | [预填充](prefill.md) |
| 对接非 Docker 缓存 | [裸机版 LANCache](bare-metal-lancache.md) |
| 在 Unraid 上部署 | [Unraid / 常见场景](recipes.md) |
| 拆分或合并缓存服务器 | [多数据源](multiple-datasources.md) |
| 调整设置与环境变量 | [配置参考](configuration-reference.md) |
| 排查无法正常工作的问题 | [故障排除](troubleshooting.md) |
