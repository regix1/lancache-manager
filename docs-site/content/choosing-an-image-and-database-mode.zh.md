<a id="image-variants"></a>

# 选择镜像与数据库模式 { #prefill-routing }

这是一个决定，而不是两个：**PostgreSQL 在哪里运行？** LANCache Manager 把它的数据库（下载历史、统计数据、游戏数据）保存在 PostgreSQL 中，镜像标签由你的答案决定。

| 模式 | 含义 | 镜像标签 |
|------|------|----------|
| **内置**（默认） | PostgreSQL 17 在 lancache-manager 容器*内部*通过 Unix 套接字运行。单容器，无需额外配置。 | `:latest` |
| **外部** | 你自己运行 PostgreSQL——边车容器、远程主机，或托管服务（RDS、Azure DB、Cloud SQL）。标准的 Docker 模式，升级也更省心。 | `:latest` 可用，也可用 `:latest-slim`（体积小约 150 MB，去掉了未使用的内置 Postgres）。需要设置 `POSTGRES_MODE=external`。 |

CI 发布的每个标签系列都遵循相同的配对（均为多架构镜像，amd64 + arm64）：

| 标签 | 说明 |
|-----|------|
| `latest` / `latest-slim` | 最新发布版本。你应该运行的版本。 |
| `1.2.0` / `1.2.0-slim` | 固定版本的发布——如果你想显式控制升级时机，就固定一个版本。 |
| `release` / `release-slim` | `latest` 的别名。 |
| `dev` / `dev-slim` | 最新开发版构建。仅用于测试——随时可能出问题。 |

```bash
# 完整版——默认，同时支持内置和外部 Postgres
docker pull ghcr.io/regix1/lancache-manager:latest

# 精简版——仅支持外部 Postgres
docker pull ghcr.io/regix1/lancache-manager:latest-slim
```

### 示例 1：内置（默认）

这就是[快速开始](quick-start.md#quick-start)中的 Compose 文件——单容器，无边车服务。可以选择性地加上一个数据库密码：

```yaml
    environment:
      # ...快速开始中的全部内容，外加：
      - POSTGRES_PASSWORD=your-secure-password
```

不设置 `POSTGRES_PASSWORD` 的话，首次运行的 UI 会提示输入。内置模式的全部设置就是这些。

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

**数据库用户权限。** 数据库用户必须是该数据库的属主，或对该数据库拥有 `CREATE` 权限。

- 表结构会安装 `citext` 扩展。PostgreSQL 只允许对数据库本身拥有 `CREATE` 权限的角色这样做。
- 只在 schema `public` 上授予 `CREATE` 是不够的，尽管从 PostgreSQL 15 不再默认授予它之后，管理员最常做的就是这项授权。
- 上面的 sidecar 示例已经满足这一点。`postgres` 镜像会让 `POSTGRES_USER` 成为 `POSTGRES_DB` 的属主。

如果这个角色不是你创建的，请这样授权：

```sql
GRANT CREATE ON DATABASE lancache TO lancache;
```

这跟超级用户无关：从 PostgreSQL 13 起，`citext` 就是受信任（trusted）扩展。如果你无法修改自己角色的权限，也可以请数据库管理员先把扩展装好，之后应用所用的角色就不需要任何额外权限：

```sql
CREATE EXTENSION IF NOT EXISTS citext;
```

有些托管服务还会对允许安装的扩展设置白名单。在 Azure Database for PostgreSQL 灵活服务器上，无论角色有什么权限，都必须先把 `citext` 加入 `azure.extensions` 服务器参数，上面两条语句才会生效。

如果权限不对，应用不会启动，也不会显示警告页面：

- 数据库初始化在 Web 服务器监听端口之前运行，因此进程会在那里停止。
- Docker 的重启策略会让它重新启动，再次遇到同样的失败。
- 浏览器显示连接被拒绝，而不是设置页面。
- 日志显示 `Database initialization failed`，后面跟着 `permission denied to create extension "citext"`。

**设置了 `POSTGRES_MODE=external` 但没有设置连接变量：** 应用会以仅设置模式启动，并显示一个 UI 表单。在那里提交的凭据会保存到 `/data/config/postgres-credentials.json`；系统会提示你重启容器以让新连接生效。

**已经在用内置数据库，想切换过去？** 现有数据不会自动迁移。请先手动导出并恢复：参见[从内置迁移到外部 PostgreSQL](https://github.com/regix1/lancache-manager/blob/main/docs/external-postgres-migration.md)。

-----
