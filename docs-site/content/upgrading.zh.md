# 升级 { #upgrading }

升级就是拉取镜像加重建容器：

```bash
docker compose pull
docker compose up -d
```

你的历史数据能保留下来，是因为它们都不在容器里。内嵌模式下，它们全部保存在 `/data` 数据卷中：数据库、API 密钥、主题、预填充状态和设置。外部模式下，请让 `/data` 和你的 PostgreSQL 存储都保持持久化。

- **标签：** `latest` 始终跟踪最新发布版本。如果你想精确控制升级时机，可以固定一个版本标签（例如 `1.10.4`），需要升级时再修改标签。
- **从旧版 SQLite 构建升级？** 请先升级到 1.10.6 或更早的版本并启动一次。该版本仍会把旧数据库导入 PostgreSQL；之后的版本不再导入，直接升级到那些版本会让旧的 `LancacheManager.db` 保持原样且不被读取。此时应用仍能正常启动，只是不会显示切换之前的历史记录，日志中会有一条警告指出该文件的位置。
- **某个版本改动了什么？** 各版本的更新说明见 [Releases 页面](https://github.com/regix1/lancache-manager/releases)。

-----
