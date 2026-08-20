# API 参考

Web 界面完全构建在 LANCache Manager 自身的 HTTP API 之上，因此凡是能点击完成的操作都可以脚本化。API 共有 284 个端点，分为六组。

## 把整套 API 交给 AI 助手

[下载 api-reference.txt](api-reference.txt){ download }（约 88 KB）

其中包含每个端点的请求方法、路径、认证要求、用途，以及请求和响应结构。把它粘贴到对话中，然后描述需求即可：

```text
附件是 LANCache 监控工具 LANCache Manager 的 API 参考。
我的实例地址是 http://cache.lan:8080，API 密钥、用户名和密码分别
保存在环境变量 LANCACHE_KEY、LANCACHE_USER 和 LANCACHE_PASSWORD 中。
请写一个 bash 脚本，为一组 app ID 启动 Steam 预填充，并等待其完成。
```

该文件由运行中的应用生成，因此描述的是代码的实际行为，而不是设计意图。

## 交互式浏览

运行中的实例在 **`/scalar`** 提供交互式参考页面，端口即你所发布的端口。如果 compose 文件映射的是 `8081:80`，地址就是 `http://<主机>:8081/scalar`。每个端点都带有 **Test Request** 按钮，可以对你的实例发起真实调用。

`/scalar` 仅限管理员访问。如果已登录应用，页面会直接加载，**Test Request** 按钮也会沿用同一个会话，无需再填写任何内容；如果尚未登录，则会跳转到登录界面。

页面顶部的 **Authentication** 面板接受 API 密钥，密钥打开的正是这个参考页面及其背后的文档。该字段中灰色显示的内容只是占位示例，并非可用密钥。

原始 OpenAPI 文档位于 **`/openapi/v1.json`**，同样仅限管理员访问，可供 Postman、Insomnia 或代码生成器使用。

!!! warning "`/swagger` 与 `/scaler` 无法使用"

    本应用使用 Scalar，因此没有 Swagger UI，而 `/scaler` 是拼写错误。两者都不会报错：无法识别的路径会交给单页应用，于是你会看到仪表板，看起来就像文档消失了。请先检查拼写。

## 身份验证

先登录一次并保存 Cookie，之后的所有调用都使用这个 Cookie 文件：

```bash
# 先取一个防伪令牌。登录本身也会修改数据，同样需要它。
curl -c jar.txt http://cache.lan:8080/api/auth/status

# 用 Cookie 文件里的令牌登录。三个字段缺一不可。
curl -b jar.txt -c jar.txt -X POST http://cache.lan:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -H "X-Antiforgery-Token: $(awk '/LancacheManager.Antiforgery/ {print $7}' jar.txt)" \
  -d "{\"apiKey\":\"$LANCACHE_KEY\",\"username\":\"operator\",\"password\":\"$LANCACHE_PASSWORD\"}"

# 之后用 Cookie 文件读取数据。
curl -b jar.txt http://cache.lan:8080/api/dashboard/batch
```

使用 `docker exec lancache-manager cat /data/security/api_key.txt` 获取密钥，也可以在**管理 → 集成**中查看，并在那里重新生成。之后的容器日志只打印提示，完整密钥仅在首次创建或轮换时写入日志。用户名和密码来自在应用中创建的账户。

读取数据只需要这个 Cookie 文件。会修改数据的请求（`POST`、`PUT`、`PATCH`、`DELETE`）还需要一个防伪令牌：用同一个 Cookie 文件调用 `GET /api/auth/status`，取出它设置的 `LancacheManager.Antiforgery` Cookie 的值，再作为 `X-Antiforgery-Token` 请求头发回。上面的登录之所以先调用状态端点，正是这个原因。令牌与签发它的会话绑定，而登录会换成一个新会话，所以在发出第一个写请求之前要再调用一次状态端点。

!!! warning "仅凭 `X-Api-Key` 已经无法调用 API"

    该请求头只能打开 `/scalar` 和 `/openapi/v1.json`，其他一概不行。对于只带这一个凭据的请求，其余端点一律返回 `401`，所以升级之后，用它轮询 `/api/cache` 或 `/api/dashboard/batch` 之类端点的脚本会立即失效。

    仍有四个初始化调用自行读取密钥，因为它们必须在任何人登录之前就能应答：`POST /api/setup/credentials` 和 `POST /api/setup/external` 从 `X-Api-Key` 请求头读取，`POST /api/account-setup/first-admin` 和 `POST /api/account-setup/recover-main-admin` 从请求体读取。

    `/metrics` 同样没有变化。它有自己的设置 `Security:RequireAuthForMetrics`，开启后仍然从请求头读取密钥，因此 Prometheus 抓取端无需改动。

!!! note "无需会话即可应答的端点"

    284 个端点中有 22 个必须在调用方尚无凭据时就能工作：登录、访客模式配置、首次运行初始化、游戏封面图、版本横幅，以及容器健康检查。它们在下载的参考文件中标记为 **public**，其余端点标记为 **requires a signed-in session**。

## 各分组的内容

| 分组 | 端点数 | 涵盖内容 |
|---|---:|---|
| Access | 64 | 登录、会话、账户、API 密钥、访客模式、用户级设置 |
| Cache and Games | 58 | 缓存内容、游戏与 depot 识别、损坏扫描、封面图 |
| Clients | 10 | 缓存客户端、客户端分组、主机名映射 |
| Downloads and Reporting | 40 | 下载历史、仪表板数据、统计、速度、事件、日志 |
| Prefill | 64 | Steam、Epic、Battle.net、Riot、Xbox 预填充守护进程及其计划任务 |
| System | 48 | 服务健康状况、指标、数据库维护、数据迁移、后台操作 |

## 几个可以先试试的调用

```bash
# 缓存大小与内容
curl -b jar.txt http://cache.lan:8080/api/cache

# 一次请求获取仪表板显示的全部内容
curl -b jar.txt http://cache.lan:8080/api/dashboard/batch

# 首次初始化状态，无需会话
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
