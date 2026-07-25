<a id="image-variants"></a>

# 选择镜像与数据库模式 { #prefill-routing }

这是一个决定，而不是两个：**PostgreSQL 在哪里运行？** LANCache Manager 把所有数据存储在 PostgreSQL 中，镜像标签由你的答案决定。

| 模式 | 含义 | 镜像标签 |
|------|------|----------|
| **内嵌**（默认） | PostgreSQL 17 在 lancache-manager 容器*内部*通过 Unix 套接字运行。单容器，无需额外配置。 | `:latest` |
| **外部** | 你自己运行 PostgreSQL——边车容器、远程主机，或托管服务（RDS、Azure DB、Cloud SQL）。标准的 Docker 模式，升级也更省心。 | `:latest` 可用，也可用 `:latest-slim`（体积小约 150 MB，去掉了未使用的内嵌 Postgres）。需要设置 `POSTGRES_MODE=external`。 |

CI 发布的每个标签系列都遵循相同的配对（均为多架构镜像，amd64 + arm64）：

| 标签 | 说明 |
|-----|------|
| `latest` / `latest-slim` | 最新发布版本。你应该运行的版本。 |
| `1.2.0` / `1.2.0-slim` | 固定版本的发布——如果你想显式控制升级时机，就固定一个版本。 |
| `release` / `release-slim` | `latest` 的别名。 |
| `dev` / `dev-slim` | 最新开发版构建。仅用于测试——随时可能出问题。 |

```bash
# 完整版——默认，同时支持内嵌和外部 Postgres
docker pull ghcr.io/regix1/lancache-manager:latest

# 精简版——仅支持外部 Postgres
docker pull ghcr.io/regix1/lancache-manager:latest-slim
```

### 示例 1：内嵌（默认）

这就是[快速开始](quick-start.md#quick-start)中的 Compose 文件——单容器，无边车服务。可以选择性地加上一个数据库密码：

```yaml
    environment:
      # ...快速开始中的全部内容，外加：
      - POSTGRES_PASSWORD=your-secure-password
```

不设置 `POSTGRES_PASSWORD` 的话，首次运行的 UI 会提示输入。内嵌模式的全部设置就是这些。

### 示例 2：外部（边车 Postgres）

两个服务：`lancache-manager` 通过 TCP 连接到 `lancache-db`。

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest-slim
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8080:80"
    volumes:
      - ./data:/data
      - /mnt/lancache/logs:/logs:ro
      - /mnt/lancache/cache:/cache:ro
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      - PUID=33
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
      - POSTGRES_MODE=external
      - POSTGRES_HOST=lancache-db
      - POSTGRES_PORT=5432
      - POSTGRES_DB=lancache
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=change-this-password
    depends_on:
      - lancache-db

  lancache-db:
    image: postgres:17-alpine
    container_name: lancache-db
    restart: unless-stopped
    environment:
      - POSTGRES_USER=lancache
      - POSTGRES_PASSWORD=change-this-password
      - POSTGRES_DB=lancache
    volumes:
      - postgres_data:/var/lib/postgresql/data

volumes:
  postgres_data:
```

`POSTGRES_PASSWORD` 必须在两个服务中保持一致。用 `docker compose up -d` 同时启动两者。

**要连接远程或托管的 Postgres？** 把 `POSTGRES_HOST` 设置为它的主机名，删除 `lancache-db` 服务，删除 `depends_on`，并省略命名数据卷。

**设置了 `POSTGRES_MODE=external` 但没设置连接变量？** 应用会以仅设置模式启动，并显示一个 UI 表单。在那里提交的凭据会保存到 `/data/config/postgres-credentials.json`；系统会提示你重启容器以让新连接生效。

**已经在用内置数据库，想切换过去？** 现有数据不会自动迁移——自动迁移只覆盖旧的 SQLite 数据库，不包括内置 Postgres。请先手动导出并恢复：参见[从内置迁移到外部 PostgreSQL](https://github.com/regix1/lancache-manager/blob/main/docs/external-postgres-migration.md)。

-----
