# 密码恢复 { #password-recovery }

当你忘记了**主管理员**密码而无法登录时，请使用此流程。

重置操作会：

- 保留账户、设置、下载记录和数据库；
- 只修改主管理员的密码；
- 注销该账户的所有现有会话；
- 清除因多次登录失败造成的锁定。

此流程不能重置其他账户。

## Docker Compose

每次容器启动时，LANCache Manager 都会把恢复脚本放入持久化数据目录。使用项目提供的 Compose 文件时，从下面两种用法中选一种。

打开一小时窗口，然后在设置屏幕上完成。浏览器会要求输入**用户名**、**密码**、**确认密码**和 **API 密钥**：

```bash
./data/scripts/reset-main-admin-password.sh
```

在命令里完成重置。请传入用户名并通过管道传入密码。密码不能作为命令行参数，因为命令行会进入 shell 历史记录，并且在命令运行期间可以从进程列表中看到：

```bash
printf %s "$NEW_PASSWORD" | ./data/scripts/reset-main-admin-password.sh --username admin --password-stdin
```

API 密钥不会出现在命令中。主机只需要 Docker；`curl`、`jq` 以及应用对外映射的端口都由容器内部处理。

新密码必须：

- 长度为 12 到 256 个字符；
- 至少使用以下四类字符中的三类：小写字母、大写字母、数字和其他字符；
- 不能与用户名相同。

## 容器使用了其他名称

默认容器名称是 `lancache-manager`。如果名称不同，请传入实际名称。

设置屏幕：

```bash
./data/scripts/reset-main-admin-password.sh --container my-lancache-manager
```

命令：

```bash
printf %s "$NEW_PASSWORD" | ./data/scripts/reset-main-admin-password.sh --container my-lancache-manager --username admin --password-stdin
```

## Unraid 或自定义数据路径

打开主机终端，找到容器配置中映射到 `/data` 的主机目录，然后运行其 `scripts` 子目录中的脚本。

设置屏幕：

```bash
/映射到/data的路径/scripts/reset-main-admin-password.sh
```

命令：

```bash
printf %s "$NEW_PASSWORD" | /映射到/data的路径/scripts/reset-main-admin-password.sh --username admin --password-stdin
```

如果容器名称不是 `lancache-manager`，请在任一用法中添加 `--container NAME`。

## 找不到脚本

当前版本的容器镜像启动时才会安装脚本。请拉取并重新创建容器，然后使用任一用法：

```bash
docker compose pull
docker compose up -d
./data/scripts/reset-main-admin-password.sh
```

```bash
docker compose pull
docker compose up -d
printf %s "$NEW_PASSWORD" | ./data/scripts/reset-main-admin-password.sh --username admin --password-stdin
```

重新创建容器不会删除 `/data` 挂载中的数据。

如果 `/data` 使用 Docker 命名卷而不是主机目录，请重启容器并在容器内运行已安装的脚本。设置屏幕用法使用 `-it`。通过管道传入密码时使用不带 TTY 的 `-i`：

```bash
docker restart lancache-manager
docker exec -it lancache-manager /data/scripts/reset-main-admin-password.sh
```

```bash
docker restart lancache-manager
printf %s "$NEW_PASSWORD" | docker exec -i lancache-manager /data/scripts/reset-main-admin-password.sh --username admin --password-stdin
```

## 裸机或源码安装

先重启 LANCache Manager。重启会打开一小时的恢复窗口，但不会自动显示恢复界面。将项目提供的 `scripts/reset-main-admin-password.sh` 放入数据目录的 `scripts` 文件夹，然后使用任一用法。如果应用监听其他地址，请修改 URL。本地模式要求该机器已安装 `curl` 和 `jq`。

设置屏幕：

```bash
/数据目录/scripts/reset-main-admin-password.sh --local --url http://127.0.0.1:8080
```

命令：

```bash
printf %s "$NEW_PASSWORD" | /数据目录/scripts/reset-main-admin-password.sh --local --url http://127.0.0.1:8080 --username admin --password-stdin
```

## 仅用单点登录的所有者 { #single-sign-on-owner }

当所有者只通过单点登录登录、没有本地密码时使用，例如环境变量中的登录已回退到 **API 密钥、用户名和密码**。所有者的用户名由服务商的颁发者和账户 ID 生成，以 `oidc-` 开头。

应用创建该账户时不会在日志中记录这个用户名，无法登录的所有者也没有能显示它的界面。请用任意 PostgreSQL 客户端从应用的 PostgreSQL 数据库中读取：

```sql
SELECT "Username" FROM "UserAccounts" WHERE "IsMainAdmin";
```

1. 重启应用。
2. 在一小时内运行主机上的恢复脚本。它会发送恢复令牌和 API 密钥来打开恢复窗口。
3. 调用 `POST /api/account-setup/recover-main-admin`，在 JSON 请求体中传入 `username`、新的 `password` 和 `apiKey`。
4. 用该密码登录。

新密码遵循与其他恢复方式相同的规则。第 3 步和第 4 步会让所有者拥有本地密码。在 `Oidc` 和 `ApiKeyOidc` 模式下，该密码可用于 `POST /api/auth/login`，见[从脚本登录](api-reference.md#signing-in-from-a-script)。

## 处理重置失败

### `401` 或 `apiKeyRequired`

数据目录中的 API 密钥未能通过验证。请确认脚本位于正在运行的应用所使用的同一个数据目录中。

### `403` 或 `recoveryWindowClosed`

一小时的恢复窗口已关闭。再次运行主机上的脚本以重启容器；裸机安装则应先重启应用，再使用 `--local`。

### `404` 或 `mainAdminNotFound`

该用户名不属于主管理员。此恢复流程不能重置其他账户。

### `400`

用户名或密码不符合上述规则。响应内容会说明具体失败规则。

### `429`

同一地址发送了过多恢复请求。请稍后再试。

!!! danger "不要删除恢复文件"

    不要通过删除数据库或 `/data/security/api_key.txt` 来恢复密码。删除 API 密钥只会创建一个新的安装密钥，不会重置任何账户密码。
