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

从 1.10.7 起，首次设置会要求您选择用户名和密码、API 密钥加用户名和密码、API 密钥加单点登录、单点登录或无需身份验证。现有实例升级后需要确认一次此选择。实例 API 密钥始终保留，用于验证所有权和恢复，并关联到第一个账户（主管理员）。只有要求 API 密钥的两种登录模式需要在登录时输入密钥。

主管理员可在 **用户 → 账户 → 访问与登录** 中更改登录方式，包括关闭或重新开启身份验证。身份验证在应用中配置；Docker Compose 的 `Security__EnableAuthentication` 开关不再覆盖已保存的选择。无需身份验证模式会向所有能访问应用的人开放管理权限。本地 HTTP 支持密码/API 密钥登录和无需身份验证访问；在不可信网络上，请使用 HTTPS 保护凭据和会话 Cookie。在外部登录服务中启用的多因素身份验证由该服务处理，本应用不会配置或强制执行。

单点登录完全自托管：应用内置 Google、GitHub、Microsoft 和 Apple 集成，并支持自定义 OpenID Connect，不依赖中央登录服务。选择预设，在相应服务中注册您自己的应用，然后输入凭据。注册设置页面显示的两个完整回调 URL，再点击**测试连接**完成真实登录。测试成功后，通过验证的身份会关联到主管理员，并启用暂存的设置；测试失败时，当前设置和已有登录连接保持不变。可以同时启用多个通过测试的服务。其他用户必须通过稳定的身份标识符明确授权，不能使用电子邮件地址或显示名称作为身份依据。

Google 和 GitHub 需要客户端 ID 和客户端密钥。Microsoft 还需要租户 ID，或使用 `consumers` 支持个人 Microsoft 账户；不支持不限定租户的 `common` 和 `organizations` 配置。Apple 需要 Apple Developer 配置中的 Services ID、Team ID、Key ID 和 `.p8` 私钥，由服务器生成短期客户端密钥。Apple 要求已注册的 HTTPS 域名，不支持 localhost 或 IP 地址回调。自定义 OpenID Connect 还需要签发者 URL。

回调 URL 是应用已经处理好的返回地址：一个用于日常登录，另一个用于测试连接。将它们复制到登录服务的应用注册配置中即可，无需自行创建或直接打开这些地址。请使用用户实际访问应用的地址打开设置。[Google 允许在本地测试时使用 HTTP localhost 和回环地址](https://developers.google.com/identity/protocols/oauth2/web-server#uri-validation)，因此浏览器与应用在同一台电脑上运行时，可以使用 `http://localhost:8080` 作为本地开发地址；在其他设备上，该地址不会指向您的服务器。其他设备使用 Google 登录时，需要 HTTPS 域名，不能直接使用局域网 IP 地址。该域名可以指向仅限局域网或 VPN 访问的服务器；使用 HTTPS 并不要求公开托管应用。其他登录服务有各自的回调要求，包括上述 Apple 的更严格限制。

更改方式后，现有账户会话仍然有效；必要时可在**用户 → 会话**中结束会话。切换到需要登录的模式后，共享的无身份验证访问将停止。通过单点登录创建的管理员必须先设置本地凭据，才能切换到密码登录模式。如果单点登录无法使用，请在主机上运行现有的 `./data/scripts/reset-main-admin-password.sh` 恢复脚本。它使用单独保存在服务器上的恢复令牌，打开恢复页面，并在重置密码后建立主管理员会话，以便修复登录方式。

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
| [Unraid / 常见场景](https://regix1.github.io/lancache-manager/zh/recipes/) | Unraid Docker 模板配置 |
| [多数据源](https://regix1.github.io/lancache-manager/zh/multiple-datasources/) | 拆分缓存或合并多台 LANCache |
| [反向代理](https://regix1.github.io/lancache-manager/zh/reverse-proxy/) | 在管理器前使用 Nginx |
| [Prometheus 指标](https://regix1.github.io/lancache-manager/zh/prometheus-metrics/) | 抓取 `/metrics` 与示例查询 |
| [API 参考](https://regix1.github.io/lancache-manager/zh/api-reference/) | 全部端点，以及可交给 AI 助手的纯文本汇总 |
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
