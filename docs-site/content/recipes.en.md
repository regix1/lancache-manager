# Recipes { #recipes }

Unraid-specific setup. For everything else, use the dedicated guides:

- [Multiple Datasources](multiple-datasources.md) - split caches or combine several LANCache servers
- [Reverse Proxy](reverse-proxy.md) - put Nginx in front of the manager
- [Prometheus Metrics](prometheus-metrics.md) - scrape `/metrics` and example queries

## Unraid { #unraid }

The repo ships a Docker template at [`unraid/lancache-manager.xml`](https://github.com/regix1/lancache-manager/blob/main/unraid/lancache-manager.xml). Save it to `/boot/config/plugins/dockerMan/templates-user/` on your Unraid box, or paste the raw-file URL into **Docker → Add Container → Template**. Then fill in the same paths and variables as the compose example. On Unraid, use `PUID=99` / `PGID=100` (the `nobody:users` default).
