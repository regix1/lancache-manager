# LANCache Manager

[English](README.md) | **中文**

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue)](LICENSE)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy_Me_A_Coffee-support_the_project-yellow)](https://www.buymeacoffee.com/regix)
[![Documentation](https://img.shields.io/badge/Docs-lancache--manager-purple)](https://regix1.github.io/lancache-manager/zh/)

[LANCache](https://lancache.net/) 的 Web 界面。无需接触终端，即可查看缓存状态并进行操作。提供英文和简体中文两种语言。

仪表板提供实时视图：节省的带宽、缓存命中率、主要客户端以及各服务的分析数据。其后是完整的缓存浏览器，包含每个已缓存游戏的封面、大小和各客户端的下载历史。Prometheus 指标开箱即用。

它也能直接操作缓存。在客人到达之前手动或按计划预填充 Steam、Epic、Battle.net、Riot 和 Xbox 游戏，并在浏览器中管理服务器：处理日志、清理缓存、检测损坏，以及在出现异常时检查 DNS 与缓存健康状况。

> [!IMPORTANT]
> **请始终拉取 `latest` 标签。** GitHub 的软件包页面会突出显示 `:dev`，因为开发版构建发布更频繁，但 `:dev` 仅供测试使用，随时可能出问题。
>
> ```bash
> docker pull ghcr.io/regix1/lancache-manager:latest
> ```

## 快速开始

你需要一个正在运行的 LANCache（本机可读取其日志和缓存目录），以及带 Compose 插件的 Docker。默认镜像自带 PostgreSQL，因此下面这一个文件就是完整的安装：

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

然后打开 `http://<主机>:8080`，并通过 `docker exec lancache-manager cat /data/security/api_key.txt` 获取 API 密钥。

完整步骤（包括最容易踩坑的缓存路径问题）请见[快速开始指南](https://regix1.github.io/lancache-manager/zh/quick-start/)。

## 文档

完整文档位于 **[regix1.github.io/lancache-manager](https://regix1.github.io/lancache-manager/zh/)**。

| 指南 | 内容 |
|---|---|
| [快速开始](https://regix1.github.io/lancache-manager/zh/quick-start/) | 安装、首次运行、API 密钥 |
| [升级](https://regix1.github.io/lancache-manager/zh/upgrading/) | 版本升级与数据保留 |
| [功能一览](https://regix1.github.io/lancache-manager/zh/what-you-get/) | 应用各个页面的介绍 |
| [预填充](https://regix1.github.io/lancache-manager/zh/prefill/) | Steam、Epic、Battle.net、Riot、Xbox 及计划任务 |
| [裸机版 LANCache](https://regix1.github.io/lancache-manager/zh/bare-metal-lancache/) | 对接非 Docker 缓存 |
| [镜像与数据库模式](https://regix1.github.io/lancache-manager/zh/choosing-an-image-and-database-mode/) | 内置与外置 PostgreSQL |
| [配置参考](https://regix1.github.io/lancache-manager/zh/configuration-reference/) | 所有环境变量与卷 |
| [常见场景](https://regix1.github.io/lancache-manager/zh/recipes/) | Unraid、多数据源、反向代理、Prometheus |
| [故障排除](https://regix1.github.io/lancache-manager/zh/troubleshooting/) | 日志不处理、游戏未识别、权限问题 |
| [自定义主题](https://regix1.github.io/lancache-manager/zh/custom-themes/) | 创建与分享主题 |
| [从源码构建](https://regix1.github.io/lancache-manager/zh/building-from-source/) | 本地开发 |
| [贡献翻译](https://regix1.github.io/lancache-manager/zh/contributing-translations/) | 新增或改进语言 |

## 支持

遇到问题？可以[提交 issue](https://github.com/regix1/lancache-manager/issues)，或到 [LanCache.NET Discord](https://discord.com/invite/BKnBS4u) 找 LANCache 社区。

如果 LANCache Manager 对你有帮助并希望支持开发，可以[请我喝杯咖啡](https://www.buymeacoffee.com/regix)。

## 许可证

采用 [GNU Affero 通用公共许可证 v3.0](LICENSE)。你可以自行托管、修改并按需使用。如果你为他人运行修改后的版本（包括作为托管服务），必须以相同许可证向这些用户提供修改版本的对应源代码。

`1.10.3` 及更早版本在 MIT 许可证下发布并继续保持 MIT。项目名称和 Logo 由[商标政策](TRADEMARK.md)保护。贡献内容需遵循[这些条款](CONTRIBUTING.md)。完整说明请见[支持与许可](https://regix1.github.io/lancache-manager/zh/support-and-license/)页面。
