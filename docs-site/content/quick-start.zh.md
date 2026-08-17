<a id="docker-compose"></a>

# 快速开始 { #quick-start }

你需要两样东西：一套正在运行的 LANCache，其日志和缓存可从这台主机读取；以及安装了 Compose 插件（`docker compose`）的 Docker。默认镜像自带 PostgreSQL，所以这一份文件就是全部安装内容：

```yaml
services:
  lancache-manager:
    image: ghcr.io/regix1/lancache-manager:latest
    container_name: lancache-manager
    restart: unless-stopped
    ports:
      - "8080:80"
    volumes:
      - ./data:/data
      - /mnt/lancache/logs:/logs:ro
      - /mnt/lancache/cache:/cache:ro
      - /var/run/docker.sock:/var/run/docker.sock  # 可选：用于预填充和日志轮转
    environment:
      - PUID=33
      - PGID=33
      - TZ=America/Chicago
      - LanCache__LogPath=/logs/access.log
      - LanCache__CachePath=/cache
```

```bash
docker compose up -d
```

!!! tip
    **启动前先检查缓存路径。** `LanCache__CachePath` 必须指向*直接包含*哈希缓存文件夹（`00`、`1a`……`ff`）的目录。标准的整体式（monolithic）LANCache 会把它们嵌套在下一层。如果你的挂载目录下只显示一个 `cache/` 文件夹，请改用 `LanCache__CachePath=/cache/cache`。详见[故障排除](troubleshooting.md#troubleshooting)。

然后：

1. 从容器内部获取你的 API 密钥：

   ```bash
   docker exec lancache-manager cat /data/security/api_key.txt
   ```

   首次启动时，密钥也会打印在容器日志中。之后的重启只打印提示，请使用该文件。

2. 打开 `http://localhost:8080`，在提示时输入该 API 密钥。
3. 首次运行时会打开设置向导。它会检查挂载权限、询问是否导入现有的日志历史，并配置 Steam depot 映射，让下载记录显示真实的游戏名称和封面图。向导支持回退上一步。

向导本身只会运行一次，但它的各个步骤之后依然可用：在**管理 → 日志与缓存**的**日志处理**卡片上，从 `⋯` 菜单选择**全部处理**可重新导入历史记录；从**管理 → 数据 → Steam 游戏映射**可刷新 depot 映射。

关于挂载的两点说明：

- 如果想从 UI 清除缓存、移除单个游戏或移除损坏文件，请去掉 `/cache` 挂载上的 `:ro`。
- Docker 套接字只在预填充和 nginx 日志轮转时才需要。未挂载时，**预填充**标签页会被隐藏而不是显示为不可用；因此如果找不到该标签页，原因就在这里。

<details markdown>
<summary><strong>想用 <code>docker run</code> 快速测试？</strong></summary>

`docker run` 的数据卷挂载需要绝对主机路径（像 `./data` 这样的相对路径只在 Compose 中有效）：

```bash
docker run -d \
  --name lancache-manager \
  -p 8080:80 \
  -v /srv/lancache-manager/data:/data \
  -v /path/to/lancache/logs:/logs:ro \
  -v /path/to/lancache/cache:/cache:ro \
  -e PUID=33 \
  -e PGID=33 \
  -e TZ=America/Chicago \
  -e LanCache__LogPath=/logs/access.log \
  -e LanCache__CachePath=/cache \
  ghcr.io/regix1/lancache-manager:latest
```

</details>

-----
