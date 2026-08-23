# 重置遗忘的管理员密码 { #password-recovery }

当你忘记了**主管理员**密码而无法登录时，请使用此流程。

重置操作会：

- 保留账户、设置、下载记录和数据库；
- 只修改主管理员的密码；
- 注销该管理员的所有现有会话；
- 清除因多次登录失败造成的锁定。

此流程不能重置其他管理员或普通用户。

## Docker Compose

每次容器启动时，LANCache Manager 都会把恢复脚本放入持久化数据目录。使用项目提供的 Compose 文件时，运行：

```bash
./data/scripts/reset-main-admin-password.sh
```

用户名和密码是可选的。如果命令里没有输入它们，脚本会提示你输入。

脚本接着会：

1. 重启 LANCache Manager 容器并打开一小时的恢复窗口；
2. 等待应用就绪；
3. 在容器内读取 API 密钥；
4. 使用你传入的用户名和密码；缺哪一项就提示输入哪一项；
5. 发送重置请求并显示结果。

API 密钥不会出现在命令中。主机只需要 Docker 和交互式终端；`curl`、`jq` 以及应用对外映射的端口都由容器内部处理。

如果愿意，也可以直接传入用户名：

```bash
./data/scripts/reset-main-admin-password.sh --username admin
```

脚本始终在提示处询问密码，不接受把密码作为命令行参数，因为命令行会进入 shell 历史记录，并且在命令运行期间可以从进程列表中看到。若要在其他脚本中调用，请通过管道传入密码：

```bash
printf %s "$NEW_PASSWORD" | ./data/scripts/reset-main-admin-password.sh --username admin --password-stdin
```

新密码必须：

- 长度为 12 到 256 个字符；
- 至少使用以下四类字符中的三类：小写字母、大写字母、数字和其他字符；
- 不能与用户名相同。

## 容器使用了其他名称

默认容器名称是 `lancache-manager`。如果名称不同，请传入实际名称：

```bash
./data/scripts/reset-main-admin-password.sh --container my-lancache-manager
```

## Unraid 或自定义数据路径

打开主机终端，找到容器配置中映射到 `/data` 的主机目录，然后运行其 `scripts` 子目录中的脚本：

```bash
/映射到/data的路径/scripts/reset-main-admin-password.sh
```

如果容器名称不是 `lancache-manager`，请添加 `--container NAME`。

## 找不到脚本

当前版本的容器镜像启动时才会安装脚本。请拉取并重新创建容器，然后运行脚本：

```bash
docker compose pull
docker compose up -d
./data/scripts/reset-main-admin-password.sh
```

重新创建容器不会删除 `/data` 挂载中的数据。

如果 `/data` 使用 Docker 命名卷而不是主机目录，请重启容器并在容器内运行已安装的脚本：

```bash
docker restart lancache-manager
docker exec -it lancache-manager /data/scripts/reset-main-admin-password.sh
```

## 裸机或源码安装

先重启 LANCache Manager，打开一小时的恢复窗口。将项目提供的 `scripts/reset-main-admin-password.sh` 放入数据目录的 `scripts` 文件夹，然后运行：

```bash
/数据目录/scripts/reset-main-admin-password.sh --local --url http://127.0.0.1:8080
```

如果应用监听其他地址，请修改 URL。本地模式要求该机器已安装 `curl` 和 `jq`。`--username` 和 `--password-stdin` 的用法与 Docker 相同。

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
