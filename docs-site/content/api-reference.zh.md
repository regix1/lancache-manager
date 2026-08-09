# API 参考

Web 界面所做的一切，都是通过调用 LancacheManager 自身的 HTTP API 完成的。这个 API 同样对你开放，因此凡是能点击完成的操作都可以脚本化：在局域网聚会前触发预填充、把缓存数据接入你自己的仪表板、用 cron 启动日志处理，或者从命令行清理某个服务的缓存。

API 共有 273 个端点，分为六组。本页说明如何调用它们，并提供一份纯文本汇总文件的下载链接，可以直接交给 AI 助手使用。

## 把整套 API 交给 AI 助手

[下载 api-reference.txt](api-reference.txt){ download }（约 84 KB）

把该文件粘贴到 Claude、ChatGPT 或你使用的其他助手对话中，然后描述你的需求即可。文件中列出了每个端点的请求方法、路径、是否需要 API 密钥、用途，以及请求和响应的结构，因此助手可以直接写出可用的脚本，而不必猜测 API 的用法或臆造并不存在的端点。

类似这样的提示词通常就够用了：

```text
附件是 LANCache 监控工具 LancacheManager 的 API 参考。
我的实例地址是 http://cache.lan:8080，API 密钥保存在环境变量
LANCACHE_KEY 中。请写一个 bash 脚本，为一组 app ID 启动 Steam
预填充，并等待其完成。
```

该文件由运行中的应用生成，因此描述的是应用的实际行为，而不是设计意图。API 发生变化时会重新生成，文件开头会标明其对应的版本。

## 交互式浏览

运行中的实例会在 **`/scalar`** 提供交互式参考页面，对于 Docker 镜像即 `http://<主机>:8080/scalar`。页面列出每个端点的请求与响应结构，并且每个端点都带有 **Test Request** 按钮，可以直接对你自己的实例发起真实调用。

`/scalar` 仅限管理员访问，请在已登录应用的浏览器标签页中打开，或者把 API 密钥填入页面顶部的 **Authentication** 面板。输入前该字段中显示的内容只是占位示例，并不是可用的密钥。

其背后的原始 OpenAPI 文档位于 **`/openapi/v1.json`**，同样仅限管理员访问。如果需要生成类型化的客户端代码，可以让 Postman、Insomnia 或代码生成器指向该地址。

## 身份验证

通过 `X-Api-Key` 请求头发送密钥：

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache
```

从容器中获取密钥：

```bash
docker exec lancache-manager cat /data/security/api_key.txt
```

在应用的**管理 → 集成**中也能看到该密钥，如果密钥泄露，可以在那里重新生成。

!!! warning "大多数端点都需要密钥，少数不需要的是有意为之"

    任何读取数据或改动安装状态的操作都需要密钥。不带密钥的请求会返回 `401`，浏览器发起的请求也一样。

    少数端点无需密钥即可响应，因为它们必须在调用方尚无任何凭据时就能工作：登录、读取访客模式配置、首次运行的初始化调用，以及容器使用的健康检查。这些端点在下载的参考文件中标记为 **public**。

    应用自身的页面使用会话 Cookie 而不是请求头进行认证，这就是界面无需在每个请求中粘贴密钥也能工作的原因。脚本应当使用请求头。

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

当前缓存大小与内容：

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/cache
```

一次请求获取仪表板显示的全部内容：

```bash
curl -H "X-Api-Key: $LANCACHE_KEY" http://cache.lan:8080/api/dashboard/batch
```

查看应用是否已完成首次初始化，该接口无需密钥：

```bash
curl http://cache.lan:8080/api/system/setup
```

预填充、缓存扫描、日志处理等长耗时任务不会阻塞请求，而是立即返回一个操作 ID。可以轮询对应的状态端点，或者订阅界面所使用的 SignalR hub 以获得推送更新。

!!! note "API 跟随应用演进，不单独进行版本管理"

    端点会随功能增加，功能被替换时也偶尔会被移除。这里没有长期兼容性承诺，也没有 `/v2`，因此请固定你所对接的镜像标签，并在升级后重新下载参考文件。

## 重新生成参考文件

面向维护者。在实例运行并持有管理员密钥的前提下：

```bash
node docs-site/generate-api-reference.mjs --key "$LANCACHE_KEY" --url http://localhost:5000
```

该命令会根据实时的 OpenAPI 文档重写 `docs-site/assets/api-reference.txt`。没有任何自动化流程会运行它，因此端点变化时需要手动重新运行，并把结果提交到仓库。
