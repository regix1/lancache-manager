# 重置丢失的管理员密码 { #password-recovery }

当你因为忘记**主管理员**密码而无法登录时，请使用本流程。

此重置操作会：

- 保留账号、设置、下载记录和数据库；
- 只更改主管理员的密码；
- 注销该管理员现有的所有会话；
- 清除因多次登录失败造成的账号锁定。

它不能重置其他管理员或普通用户账号。

## 开始前需要准备

请在运行 LANCache Manager 的机器上执行命令。你需要：

1. 主管理员的用户名；
2. Docker 主机的操作权限；
3. `curl` 和 `jq`。

开始前检查所需命令：

```bash
docker compose version
curl --version
jq --version
```

如果 `curl` 或 `jq` 显示 `command not found`，请先使用操作系统的软件包管理器安装它。

## Docker Compose：逐步操作

### 1. 打开 Compose 文件夹

进入实际用于运行 LANCache Manager 的 `docker-compose.yml` 所在文件夹：

```bash
cd /path/to/your/lancache-manager-folder
```

确认 Compose 能识别该服务：

```bash
docker compose config --services
```

输出中应包含：

```text
lancache-manager
```

### 2. 重启 LANCache Manager

```bash
docker compose restart lancache-manager
```

重启后，密码重置端点会开放**一小时**。重启不会删除数据。

### 3. 读取 API 密钥

```bash
LCM_API_KEY="$(docker compose exec -T lancache-manager cat /data/security/api_key.txt)"
```

密钥会保存在 `LCM_API_KEY` shell 变量中。该命令不会在屏幕上打印任何内容，这是正常现象。

### 4. 设置应用地址

项目提供的 Compose 文件将 LANCache Manager 发布到 `8080` 端口：

```bash
LCM_URL="http://127.0.0.1:8080"
```

如果你的 Compose 文件使用了其他端口，请把 `8080` 替换成主机端口。例如，映射 `9090:80` 应使用 `http://127.0.0.1:9090`。

等待重启后的应用准备就绪：

```bash
until curl --fail --silent "$LCM_URL/health" >/dev/null; do sleep 2; done
```

应用准备好后，该命令会静默结束。如果一直等待，请按 `Ctrl+C` 停止；这通常表示 `LCM_URL` 中的端口不正确。

### 5. 输入用户名和新密码

```bash
read -r -p "主管理员用户名: " LCM_USERNAME
read -r -s -p "新密码: " LCM_PASSWORD
printf '\n'
```

输入密码时屏幕不会显示字符，这是正常现象。

新密码必须：

- 长度为 12 至 256 个字符；
- 至少使用以下四类字符中的三类：小写字母、大写字母、数字和其他字符；
- 不能与用户名相同。

例如，`FreshPassword2026` 包含小写字母、大写字母和数字。不要把这个示例用作你的真实密码。

### 6. 发送重置请求

复制并运行以下整个命令块，不要修改端点地址：

```bash
jq -n \
  --arg apiKey "$LCM_API_KEY" \
  --arg username "$LCM_USERNAME" \
  --arg password "$LCM_PASSWORD" \
  '{apiKey: $apiKey, username: $username, password: $password}' |
curl --fail-with-body --silent --show-error \
  -X POST "$LCM_URL/api/account-setup/recover-main-admin" \
  -H 'Content-Type: application/json' \
  --data-binary @-
```

`jq` 会安全地生成 JSON 请求，`curl` 会把请求发送给 LANCache Manager。

重置成功时会显示：

```json
{"success":true,"message":"Password reset"}
```

清除临时 shell 变量：

```bash
unset LCM_API_KEY LCM_USERNAME LCM_PASSWORD
```

现在可以使用新密码登录，旧密码将不再有效。

## 不使用 Compose 的 Docker 或 Unraid

按照上面的第 3 至第 6 步操作，但使用容器名重启并读取密钥：

```bash
docker restart lancache-manager
LCM_API_KEY="$(docker exec lancache-manager cat /data/security/api_key.txt)"
```

如果容器使用了其他名称，请替换 `lancache-manager`。将 `LCM_URL` 设置为该容器分配的主机端口。

在 Windows Git Bash 中，使用以下命令读取密钥：

```bash
LCM_API_KEY="$(MSYS_NO_PATHCONV=1 docker exec lancache-manager cat /data/security/api_key.txt)"
```

## 裸机或源码安装

1. 重启 LANCache Manager 进程或系统服务。
2. 读取应用数据目录下的 `security/api_key.txt`。
3. 将该值保存到 `LCM_API_KEY`。
4. 将 `LCM_URL` 设置为 API 实际监听的地址。
5. 按照上面的第 5 和第 6 步操作。

## 解决请求失败

### `401` 或 `apiKeyRequired`

重新读取 `/data/security/api_key.txt` 中的当前密钥。密钥必须放在 JSON 请求中。为密码恢复请求添加 `X-Api-Key` 请求头不会生效。

### `403` 或 `recoveryWindowClosed`

应用启动后已经超过一小时。重启 LANCache Manager，然后再次发送请求。

### `404` 或 `mainAdminNotFound`

该用户名不属于主管理员。检查用户名后重试。此端点不能重置其他账号。

### `400`

用户名或密码不符合第 5 步所列规则。响应内容会说明具体违反了哪一条规则。

### `429`

同一地址发送了过多恢复请求。请等待一段时间后再试。

!!! danger "不要删除恢复文件"

    不要通过删除数据库或 `/data/security/api_key.txt` 来恢复密码。删除 API 密钥只会创建一个新的安装密钥，不会重置任何账号密码。
