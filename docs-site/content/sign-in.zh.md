# 登录与访问 { #sign-in }

首次运行时需要选择他人如何登录。升级的实例会确认一次该选择。之后可在 **用户 → 账户 → 访问与登录** 中更改。

## 五种模式

| 模式 | 用户需输入 | 适用场景 |
|---|---|---|
| 用户名和密码 | 密码 | 大多数部署 |
| API 密钥 + 密码 | 先密钥，后密码 | 在登录页增加一道关卡 |
| API 密钥 + 单点登录 | 先密钥，后 SSO | 同上，配合外部服务商 |
| 单点登录 | 仅 SSO | Google、GitHub、Microsoft、Apple 或 OIDC |
| 免认证访问 | 无需输入 | 仅限受信任的局域网 |

免认证访问会让任何能访问该应用的人获得管理权限。

!!! note "API 密钥始终有效"
    它属于第一个账户，即主管理员，始终可用于所有权确认和账户恢复。只有两种 API 密钥模式会在登录时要求输入它。

    ```bash
    docker exec lancache-manager cat /data/security/api_key.txt
    ```

`Security__EnableAuthentication` 这个 compose 变量不再覆盖已保存的选择。在受信任的网络中，密码和 API 密钥模式使用 HTTP 即可；其他环境请使用 HTTPS，以免凭据和会话 Cookie 以明文传输。

## 单点登录

这里没有中心登录服务。你需要在服务商处注册一个应用，然后把它的凭据填进来。

| 服务商 | 所需信息 |
|---|---|
| Google、GitHub | 客户端 ID、客户端密钥 |
| Microsoft | 客户端 ID、客户端密钥、租户 ID（个人账户填 `consumers`） |
| Apple | Services ID、Team ID、Key ID、`.p8` 私钥 |
| 自定义 OIDC | 客户端 ID、客户端密钥、颁发者 URL |

不支持 Microsoft 与租户无关的 `common` 和 `organizations` 取值。Apple 要求已注册的 HTTPS 域名，不接受 localhost 和 IP 地址。

设置页会显示两个回调 URL，一个用于日常登录，一个用于测试。请把两个都复制到服务商的应用注册中；你无需自己打开它们。请使用他人实际访问该应用的地址来打开设置页，因为回调地址正是基于该地址生成的。

然后运行 **测试连接**，它会执行一次真实登录。通过后会把已验证的身份关联到主管理员并启用这些设置。失败则不改变任何内容，因此一次失败的尝试不会破坏已经可用的配置。可以同时启用多个服务商。

其他用户需通过稳定的身份标识符授权，而不是电子邮件地址或显示名称。

### 回调地址

[Google 允许 localhost 和回环地址使用 HTTP](https://developers.google.com/identity/protocols/oauth2/web-server#uri-validation)，因此当浏览器和应用在同一台机器上时，`http://localhost:8080` 可用。但它无法从其他设备访问。若需从其他设备登录，请使用 HTTPS 域名而非局域网 IP 地址。该域名可以指向仅在局域网或 VPN 内可达的服务器，因此使用 HTTPS 并不意味着必须公网托管。

## 切换模式

更改模式后现有会话仍然有效；可在 **用户 → 会话** 中结束它们。一旦选择了某种登录模式，共享的免认证访问即告停止。通过 SSO 创建的管理员必须先设置本地凭据，才能切换到密码模式。

如果 SSO 出现故障导致你无法登录，请在宿主机上运行恢复脚本：

```bash
./data/scripts/reset-main-admin-password.sh
```

它使用服务器端单独保存的令牌打开恢复页面，并在密码重置后让你以主管理员身份登录，从而修复登录方式。详见[密码恢复](password-recovery.md)。
