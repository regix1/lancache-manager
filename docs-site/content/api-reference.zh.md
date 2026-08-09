# API 参考

Web 界面完全构建在 LancacheManager 自身的 HTTP API 之上，因此凡是能点击完成的操作都可以脚本化。API 共有 273 个端点，分为六组。

## 把整套 API 交给 AI 助手

[下载 api-reference.txt](api-reference.txt){ download }（约 84 KB）

其中包含每个端点的请求方法、路径、认证要求、用途，以及请求和响应结构。把它粘贴到对话中，然后描述需求即可：

```text
附件是 LANCache 监控工具 LancacheManager 的 API 参考。
我的实例地址是 http://cache.lan:8080，API 密钥保存在环境变量
LANCACHE_KEY 中。请写一个 bash 脚本，为一组 app ID 启动 Steam
预填充，并等待其完成。
```

该文件由运行中的应用生成，因此描述的是代码的实际行为，而不是设计意图。

## 交互式浏览

运行中的实例在 **`/scalar`** 提供交互式参考页面，端口即你所发布的端口。如果 compose 文件映射的是 `8081:80`，地址就是 `http://<主机>:8081/scalar`。每个端点都带有 **Test Request** 按钮，可以对你的实例发起真实调用。

`/scalar` 仅限管理员访问。如果已登录应用，页面会直接加载；如果尚未登录，则会跳转到登录界面。你也可以把密钥填入页面顶部的 **Authentication** 面板，该字段中灰色显示的内容只是占位示例，并非可用密钥。

原始 OpenAPI 文档位于 **`/openapi/v1.json`**，同样仅限管理员访问，可供 Postman、Insomnia 或代码生成器使用。

!!! warning "`/swagger` 与 `/scaler` 无法使用"

    本应用使用 Scalar，因此没有 Swagger UI，而 `/scaler` 是拼写错误。两者都不会报错：无法识别的路径会交给单页应用，于是你会看到仪表板，看起来就像文档消失了。请先检查拼写。

## 身份验证

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache
```

使用 `docker exec lancache-manager cat /data/security/api_key.txt` 获取密钥，也可以在**管理 → 集成**中查看，并在那里重新生成。

!!! warning "大多数端点都需要密钥"

    任何读取数据或改动安装状态的请求，不带密钥都会返回 `401`，浏览器发起的请求也一样。

    少数端点无需密钥，因为它们必须在调用方尚无凭据时就能工作：登录、访客模式配置、首次运行初始化，以及容器健康检查。这些端点在下载的参考文件中标记为 **public**。

    应用自身的页面使用会话 Cookie 而不是请求头。脚本应当使用请求头。

## 各分组的内容

| 分组 | 端点数 | 涵盖内容 |
|---|---:|---|
| Access | 53 | 登录、会话、API 密钥、访客模式、用户级设置 |
| Cache and Games | 58 | 缓存内容、游戏与 depot 识别、损坏扫描、封面图 |
| Clients | 10 | 缓存客户端、客户端分组、主机名映射 |
| Downloads and Reporting | 40 | 下载历史、仪表板数据、统计、速度、事件、日志 |
| Prefill | 64 | Steam、Epic、Battle.net、Riot、Xbox 预填充守护进程及其计划任务 |
| System | 48 | 服务健康状况、指标、数据库维护、数据迁移、后台操作 |

## 几个可以先试试的调用

```bash
# 缓存大小与内容
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache

# 一次请求获取仪表板显示的全部内容
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/dashboard/batch

# 首次初始化状态，无需密钥
curl http://cache.lan:8080/api/system/setup
```

预填充、缓存扫描和日志处理不会阻塞请求，而是立即返回操作 ID。可以轮询对应的状态端点，或订阅界面所使用的 SignalR hub。

!!! note "API 不单独进行版本管理"

    端点会随功能增加，功能被替换时也会被移除。这里没有 `/v2`，也没有兼容性承诺，因此请固定所对接的镜像标签，并在升级后重新下载参考文件。

## 重新生成参考文件

面向维护者，在实例运行的前提下：

```bash
node docs-site/generate-api-reference.mjs --key "$LANCACHE_KEY" --url http://localhost:5000
```

该命令会重写 `docs-site/assets/api-reference.txt`。没有自动化流程会运行它，因此端点变化时需手动重新运行并提交结果。
