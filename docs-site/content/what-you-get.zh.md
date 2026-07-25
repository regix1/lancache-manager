# 功能一览 { #screenshots }

主要页面速览。所有截图均使用默认的深色主题。

### 仪表板

<div align="center">
<img alt="仪表板概览，显示节省的带宽、缓存命中率、服务分布和热门客户端" src="images/dashboard-overview.png" />

*仪表板——节省的带宽、命中率、服务分析和热门客户端，一屏尽览*
</div>

### 下载

<div align="center">
<img alt="下载页面普通视图，显示带缓存命中进度的按游戏下载卡片" src="images/downloads-normal.png" />

*下载——每个已缓存游戏的封面图、大小和按客户端的历史记录*
</div>

三种视图模式：**标准**（卡片视图，如上图）、**紧凑**（密集列表）和**复古**（90 年代 BBS 风格的按 depot 表格）。命中/未命中筛选和按客户端筛选可以缩小列表范围。

### 客户端

<div align="center">
<img alt="客户端页面列出设备及各自的下载总量、缓存命中与未命中数、命中率" src="images/clients.png" />

*客户端——哪些设备在使用缓存，以及缓存为每个设备服务得如何*
</div>

想知道哪些设备下载量最大、它们的安装是否真正命中了缓存时，打开这个页面。

### 用户

<div align="center">
<img alt="用户页面显示当前活跃会话列表" src="images/users-sessions.png" />

*用户——活跃会话与访客访问*
</div>

访客访问就在这里管理：查看活跃会话，无需分享你的 API 密钥即可授予限时的只读访问权限。

### 事件

<div align="center">
<img alt="事件日历，显示已安排的一场 LAN 聚会活动" src="images/events.png" />

*事件——在日历上查看下载活动和 LAN 聚会*
</div>

正在筹备一场 LAN 聚会？把它加到日历上，就能在日期上下文中查看下载活动。

### 状态检查 { #status-check }

<div align="center">
<img alt="状态检查标签页，显示 DNS 解析结果与缓存域名验证" src="images/status-check.png" />

*状态检查——验证 DNS、缓存可达性和近期下载的路由情况*
</div>

状态检查标签页（管理 → 状态检查）无需打开终端，就能回答"我的 LANCache 到底有没有在工作？"。它会检查游戏域名是否解析到你的缓存、缓存是否响应、以及近期下载是否真的经过了缓存——按域名逐一给出，语言通俗易懂。

<details>
<summary><strong>"来自此设备"为什么显示<em>无法确定</em>，以及如何解决</strong></summary>

这一个探测由你的浏览器发起，而不是服务器，因此它反映的是你的客户端所看到的情况。它会请求 `http://lancache.steamcontent.com/lancache-heartbeat`，真正的缓存节点会返回 `204`，并带上标明自身名称的 `X-LanCache-Processed-By` 响应头。

除非缓存主动开放，否则浏览器不允许页面跨源读取该响应头，而默认情况下缓存并未开放。于是探测会退回为一次只能证明*有东西*响应、却无法得知*是什么*响应的请求，卡片显示**无法确定**，并在浏览器控制台记录一条 CORS 错误。这并没有出现故障。状态检查的其他所有结果都来自服务端，不受影响。

若想得到明确结果，可以让缓存开放该响应头。在缓存的 nginx 配置中，向 heartbeat 的 location 添加以下几行：

```nginx
location /lancache-heartbeat {
    add_header X-LanCache-Processed-By $hostname always;
    add_header Access-Control-Allow-Origin "*" always;
    add_header Access-Control-Expose-Headers "X-LanCache-Processed-By" always;
    return 204;
}
```

`Access-Control-Expose-Headers` 是最容易被遗漏的一行。缺少它时请求虽然成功，响应头依然无法读取，卡片仍会显示无法确定。这里使用 `*` 是安全的：该探测不发送 Cookie 或凭据，响应中也只包含节点名称。

有两点需要注意。如果你通过 **https** 提供本管理器，浏览器会直接阻止这个明文 http 探测，无论是否配置 CORS，该卡片都无法工作。另外，如果探测返回的不是 `204`（例如 `403`），那就不是 CORS 问题，而是一个真实的发现：该域名解析到的并不是你的缓存。

</details>

### 日志与缓存

<div align="center">
<img alt="日志与缓存管理页面，显示日志处理和缓存操作控件" src="images/management-logs-cache.png" />

*管理 → 日志与缓存——处理日志、管理磁盘缓存，并检测损坏或已失效的文件*
</div>

**损坏扫描。** 两种扫描，对应两类不同的问题。*重复未命中*扫描会读取日志，找出磁盘上存在却反复未命中的文件，这通常意味着缓存副本已损坏。*结构性*扫描则会直接打开缓存文件，检查 nginx 响应头、载荷偏移和记录的长度，只标记确凿的失败项。结构性扫描分为**全面扫描**（扫描所有符合条件的文件并重建基线）和**增量扫描**（只扫描新增、变更或此前未解决的文件）两种模式。要移除扫描发现的任何文件，`/cache` 挂载都不能带 `:ro`。

以上是日常会用到的部分。管理页面背后还有更多标签——展开下方查看全部内容。

<details>
<summary><strong>查看全部管理页面</strong></summary>

#### 设置

<div align="center">
<img alt="管理设置标签页，包含 API 认证、演示模式和显示偏好设置" src="images/management-settings.png" />

*设置——认证、演示模式和显示偏好设置。*
</div>

**演示模式**会用模拟数据填充界面，让你在还没有任何真实缓存历史时就能先试用 UI。

#### 集成

<div align="center">
<img alt="集成标签页，显示全部五个游戏平台的登录卡片和 Prometheus 端点面板" src="images/management-integrations.png" />

*集成——登录游戏平台并配置 Prometheus 端点。一个页面即可查看全部五项预填充服务的登录状态。*
</div>

#### 数据

<div align="center">
<img alt="数据标签页，包含 Steam 游戏映射卡片和数据库导入表单" src="images/management-data.png" />

*数据——Steam 游戏映射与数据库导入。*
</div>

#### 计划任务

<div align="center">
<img alt="计划任务标签页，显示每个后台服务的计划卡片，含间隔和立即运行按钮" src="images/management-schedules-system.png" />

*计划任务——每个后台服务都有自己的运行间隔和一个"立即运行"按钮。计划预填充卡片位于本页底部，会在下方的预填充章节中展示。*
</div>

#### 主题

<div align="center">
<img alt="主题图库，包含已安装主题、社区主题和自定义主题上传区域" src="images/management-theme.png" />

*主题——在已安装的主题间切换、导入社区主题，或上传你自己的主题。*
</div>

#### 客户端（别名与排除）

<div align="center">
<img alt="管理客户端标签页，用于分配昵称并将设备排除在统计之外" src="images/management-client-aliases.png" />

*客户端——为设备指定友好名称，并将某些设备排除在统计之外。*
</div>

一个昵称可以覆盖多个 IP 地址——当一台机器双系统启动，或在有线与无线之间切换、否则会被识别为多个客户端时，这很有用。

#### 预填充会话

<div align="center">
<img alt="预填充会话标签页，显示实时、持久和历史预填充容器会话" src="images/management-prefill-sessions.png" />

*预填充会话——查看实时和持久预填充容器，并回顾历史运行记录。*
</div>

</details>

-----
