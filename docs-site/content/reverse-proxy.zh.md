# 反向代理 { #nginx-reverse-proxy }

LANCache Manager 可以在 nginx 后面正常运行。建议使用 HTTPS。

!!! tip
    在管理器前面架设了代理？记得同时设置 `Security__KnownProxyNetworks`（见[安全](configuration-reference.md#security)），这样客户端 IP 才能被正确报告。

### 单一来源（推荐）

从同一个主机名同时提供 UI 和 API。Cookie 保持第一方，CORS 也就不成问题。

```nginx
server {
  listen 443 ssl http2;
  server_name lancache.example.com;

  ssl_certificate     /etc/letsencrypt/live/lancache.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/lancache.example.com/privkey.pem;

  # 请求体大小上限——如果大文件上传（比如数据库导入）返回 413，就调高此值
  client_max_body_size 50m;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;

    # SignalR（WebSocket）
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 600s;  # 为缓慢时段留出余量；SignalR 每 10 秒的保活消息通常足以让连接保持存活
  }
}

server {
  listen 80;
  server_name lancache.example.com;
  return 301 https://$host$request_uri;
}
```

### 分离的 API 来源（仅在必要时使用）

如果 UI 和 API 位于不同的主机名：

- 用 `VITE_API_URL=https://api.lancache.example.com` 构建 UI。
- 把 UI 来源加入 `Security__AllowedOrigins`，让 CORS 允许它携带凭据。
- 让两个主机名保持在同一个可注册域名之下（如本例所示）。LANCache Manager 签发的是 `SameSite=Lax` Cookie，浏览器只在两个主机名同属一个站点时才会发送这类 Cookie——API 位于完全不同的域名时无法使用 Cookie 登录。

```nginx
server {
  listen 443 ssl http2;
  server_name api.lancache.example.com;

  ssl_certificate     /etc/letsencrypt/live/api.lancache.example.com/fullchain.pem;
  ssl_certificate_key /etc/letsencrypt/live/api.lancache.example.com/privkey.pem;

  location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;

    # SignalR（WebSocket）
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 600s;  # 为缓慢时段留出余量；SignalR 每 10 秒的保活消息通常足以让连接保持存活
  }
}
```
