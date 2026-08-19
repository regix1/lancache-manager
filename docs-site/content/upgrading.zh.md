# 升级 { #upgrading }

升级就是拉取镜像加重建容器：

```bash
docker compose pull
docker compose up -d
```

你的历史数据能保留下来，是因为它们都不在容器里。内嵌模式下，它们全部保存在 `/data` 数据卷中：数据库、API 密钥、主题、预填充状态和设置。外部模式下，请让 `/data` 和你的 PostgreSQL 存储都保持持久化。

- **标签：** `latest` 始终跟踪最新发布版本。如果你想精确控制升级时机，可以固定一个版本标签（例如 `1.10.4`），需要升级时再修改标签。
- **从旧版 SQLite 构建升级？升级前请先阅读。** 1.10.6 之后的版本不再导入旧数据库，并且会在**首次启动时将其删除**。直接升级到这些版本会永久销毁切换之前的所有下载记录，且无法恢复。如果你需要这些历史记录，请先升级到 1.10.6 并启动一次以完成导入，确认下载记录已存在后再继续升级。如果不需要，可以直接升级，旧文件会被自动清理。
- **会被清理的内容。** 首次启动时，应用会删除 `/data/db/LancacheManager.db` 及其 `-wal`/`-shm` 附属文件（更早的目录结构则位于 `/data/LancacheManager.db`）、标记文件 `/data/postgres-migration.complete` 和 `/data/postgresql/.migration_complete`，以及位于 `/data/state/corruption-structural` 的旧损坏扫描基线。每次删除都会记录在日志中。
- **某个版本改动了什么？** 各版本的更新说明见 [Releases 页面](https://github.com/regix1/lancache-manager/releases)。

-----
