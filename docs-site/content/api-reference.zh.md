# API 参考

Web 界面完全构建在 LANCache Manager 自身的 HTTP API 之上，因此凡是能点击完成的操作都可以脚本化。API 共有 283 个端点，分为六组。

## 下载端点列表

[下载 api-reference.txt](api-reference.txt){ download }（约 88 KB）

其中包含每个端点的请求方法、路径、认证要求、用途，以及请求和响应结构。该文件由运行中的应用生成，因此描述的是代码的实际行为，而不是设计意图。

## 交互式浏览

运行中的实例在 **`/scalar`** 提供交互式参考页面，端口即你所发布的端口。如果 compose 文件映射的是 `8081:80`，地址就是 `http://<主机>:8081/scalar`。每个端点都可以对你的实例发起真实调用。

`/scalar` 需要已登录的账户。如果已登录应用，页面会直接加载，这些调用也会沿用同一个会话，无需再填写任何内容；如果尚未登录，则会跳转到登录界面。

页面顶部的 API 密钥字段接受 API 密钥，密钥打开的正是这个参考页面及其背后的文档。该字段中灰色显示的内容只是占位示例，并非可用密钥。

原始 OpenAPI 文档位于 **`/openapi/v1.json`**，同样需要已登录的账户，可供 Postman、Insomnia 或代码生成器使用。

!!! warning "`/swagger` 与 `/scaler` 无法使用"

    本应用使用 Scalar，因此没有 Swagger UI，而 `/scaler` 是拼写错误。两者都不会报错：无法识别的路径会交给单页应用，于是你会看到仪表板，看起来就像文档消失了。请先检查拼写。

## 身份验证

脚本先登录一次，之后的每个请求都带上会话 Cookie。仅凭 API 密钥无法让脚本登录。

!!! warning "v1.10.6 起的变化：仅带 `X-Api-Key` 的请求在大多数路由上返回 `401`"
    v1.10.6 之前，带上 `X-Api-Key` 的请求会以管理员身份通过所有端点。升级后，仍只用该请求头轮询 `/api/cache` 或 `/api/dashboard/batch` 的脚本会收到 `401`。请改用下面的登录步骤。

### API 密钥仍然有效的位置

| 路由 | 密钥的传递方式 |
|---|---|
| `GET /scalar` 和 `GET /openapi/v1.json` | `X-Api-Key` 请求头 |
| `/metrics`（开启 `Security:RequireAuthForMetrics` 时） | `X-Api-Key` 请求头。Prometheus 抓取端无需改动。 |
| `POST /api/setup/credentials`、`POST /api/setup/external` | `X-Api-Key` 请求头 |
| `POST /api/account-setup/first-admin`、`POST /api/account-setup/open-main-admin-recovery`、`POST /api/account-setup/recover-main-admin` | 请求体中的 `apiKey` 字段 |
| 其他所有路由 | 不接受，返回 `401` |

初始化和恢复调用保留密钥，因为它们必须在任何账户能登录之前就可用。

### 为什么密钥不再让脚本登录

- 密钥是整个实例共用的一个密钥，在创建或轮换时会出现在容器日志中。
- 只带密钥的请求不会留下会话，应用无法记录是谁发出的。
- 泄露的密钥无法单独从某个调用方收回。唯一的办法是重新生成，这会影响所有用户。
- 已登录的会话会列在**用户 → 会话**中，并可以单独结束。

### 从脚本登录

登录调用需要的内容取决于访问模式（见[访问与登录](sign-in.md)）：

| 访问模式 | `POST /api/auth/login` 的请求体 |
|---|---|
| 用户名和密码 | `username`、`password` |
| API 密钥、用户名和密码 | `apiKey`、`username`、`password` |
| 免认证访问 | 仅限主管理员的 `username`、`password` |
| API 密钥和单点登录 | 不可用，调用会被拒绝 |
| 单点登录 | 不可用，调用会被拒绝 |

在两种单点登录模式下，脚本无法登录。

以 **API 密钥、用户名和密码** 模式为例：

1. 获取防伪令牌。登录会修改状态，因此也需要它。
2. 登录，并把 Cookie 保存到文件。
3. 之后的每个请求都带上这个 Cookie 文件。

```bash
# 1. 防伪令牌
curl -c jar.txt http://cache.lan:8080/api/auth/status

# 2. 登录。「用户名和密码」模式不传 apiKey。
curl -b jar.txt -c jar.txt -X POST http://cache.lan:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -H "X-Antiforgery-Token: $(awk '/LancacheManager.Antiforgery/ {print $7}' jar.txt)" \
  -d "{\"apiKey\":\"$LANCACHE_KEY\",\"username\":\"operator\",\"password\":\"$LANCACHE_PASSWORD\"}"

# 3. 用 Cookie 文件读取数据
curl -b jar.txt http://cache.lan:8080/api/dashboard/batch
```

把 `operator`、`$LANCACHE_KEY` 和 `$LANCACHE_PASSWORD` 换成在应用中创建的账户及其密码。最后一条命令会以 JSON 返回仪表板数据。

### 会修改数据的请求

读取只需要 Cookie 文件。`POST`、`PUT`、`PATCH` 和 `DELETE` 还需要防伪令牌：

1. 用同一个 Cookie 文件调用 `GET /api/auth/status`。
2. 取出它设置的 `LancacheManager.Antiforgery` Cookie 的值。
3. 把该值放在 `X-Antiforgery-Token` 请求头中发送。

令牌属于某一个会话，而登录会开始一个新会话。登录之后、第一个写请求之前，请重新获取令牌。

### 获取密钥

```bash
docker exec lancache-manager cat /data/security/api_key.txt
```

- **管理 → 设置**中的**重新生成 API 密钥**会替换密钥，旧密钥随即失效。
- 完整密钥只在创建或轮换时出现在容器日志中，之后的启动只打印提示。
- 如果主管理员密码丢失，请在主机上运行 `./data/scripts/reset-main-admin-password.sh`。见[密码恢复](password-recovery.md)。

!!! note "无需会话即可应答的端点"

    283 个端点中有 23 个必须在调用方尚无凭据时就能工作：登录、访客模式配置、首次运行初始化、密码恢复、游戏封面图、版本横幅，以及容器健康检查。它们在下载的参考文件中标记为 **public**，其余端点标记为 **requires a signed-in session**。

## 各分组的内容

| 分组 | 端点数 | 涵盖内容 |
|---|---:|---|
| Access | 65 | 登录、会话、账户、API 密钥、访客模式、用户级设置 |
| Cache and Games | 58 | 缓存内容、游戏与 depot 识别、损坏扫描、封面图 |
| Clients | 10 | 缓存客户端、客户端分组、主机名映射 |
| Downloads and Reporting | 40 | 下载历史、仪表板数据、统计、速度、事件、日志 |
| Prefill | 62 | Steam、Epic、Battle.net、Riot、Xbox 预填充守护进程及其计划任务 |
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
