# Bandwidth Speed Monitoring - Research & Implementation Plan

## ✅ IMPLEMENTED

### Files Created/Modified:

**Rust Processor:**
- `rust-processor/src/models.rs` - Added `StreamLogEntry` struct
- `rust-processor/src/stream_parser.rs` - Parser for stream-access.log format
- `rust-processor/src/stream_processor.rs` - Binary to process stream logs
- `rust-processor/Cargo.toml` - Added stream_processor binary

**C# API:**
- `Models/StreamSession.cs` - Entity for stream sessions
- `Models/Download.cs` - Added speed columns (DownloadSpeedBps, UploadSpeedBps, etc.)
- `Data/AppDbContext.cs` - Added StreamSessions DbSet
- `Migrations/20251220200000_AddSpeedDataSupport.cs` - Database migration
- `Infrastructure/Services/RustStreamProcessorService.cs` - C# service to run Rust processor
- `Controllers/StreamLogsController.cs` - API endpoints
- `Program.cs` - Registered stream service

**Frontend:**
- `Web/src/services/api.service.ts` - Added stream log API methods
- `Web/src/components/features/management/log-processing/StreamLogsManager.tsx` - UI component
- `Web/src/components/features/management/sections/StorageSection.tsx` - Added to Storage tab

---

## ✅ SOLUTION FOUND: stream-access.log

Lancache has a **separate log file** called `stream-access.log` that contains session timing data!

### Stream Log Format

```
172.16.1.143 [20/Dec/2025:07:50:58 -0600] TCP 200 7063 910 77.590 "cp601.prod.do.dsp.mp.microsoft.com"
│            │                          │   │   │    │   │       │
│            │                          │   │   │    │   │       └── upstream_host
│            │                          │   │   │    │   └── session_duration (seconds)
│            │                          │   │   │    └── bytes_received (from client/upstream)
│            │                          │   │   └── bytes_sent (to client/downstream)
│            │                          │   └── status
│            │                          └── protocol (TCP/UDP)
│            └── timestamp (end of session)
└── client_ip
```

### Speed Calculation

```
bytes_sent / session_duration = download_speed

Example: 283399 bytes / 15.520 sec = 18,261 bytes/sec ≈ 17.8 KB/s
```

### What We Can Now Calculate

| Metric | Formula | Example |
|--------|---------|---------|
| Download speed | bytes_sent / duration | 283399 / 15.52 = 18.3 KB/s |
| Upload speed | bytes_received / duration | 91106 / 15.52 = 5.9 KB/s |
| Session duration | duration field | 15.52 seconds |
| Total transferred | bytes_sent + bytes_received | 374,505 bytes |

---

## Implementation Plan

### Phase 1: Parse stream-access.log in Rust

**Files to handle:**
- `stream-access.log` (current)
- `stream-access.log.1`, `.2`, `.3`, etc. (rotated logs)

**New struct:**
```rust
pub struct StreamLogEntry {
    pub client_ip: String,
    pub timestamp: NaiveDateTime,      // End of session
    pub protocol: String,              // TCP/UDP
    pub status: i32,
    pub bytes_sent: i64,               // To client (download)
    pub bytes_received: i64,           // From client (upload)
    pub session_duration: f64,         // Seconds
    pub upstream_host: String,
}

// Calculated fields
impl StreamLogEntry {
    pub fn download_speed_bps(&self) -> f64 {
        self.bytes_sent as f64 / self.session_duration
    }

    pub fn upload_speed_bps(&self) -> f64 {
        self.bytes_received as f64 / self.session_duration
    }

    pub fn session_start(&self) -> NaiveDateTime {
        self.timestamp - Duration::seconds(self.session_duration as i64)
    }
}
```

**Regex pattern:**
```rust
r#"^(?P<ip>\S+)\s+\[(?P<time>[^\]]+)\]\s+(?P<protocol>\S+)\s+(?P<status>\d+)\s+(?P<bytes_sent>\d+)\s+(?P<bytes_recv>\d+)\s+(?P<duration>[\d.]+)\s+"(?P<host>[^"]+)"$"#
```

### Phase 2: Map Stream Sessions to Access Log Downloads

**Challenge:** Match stream entries to access.log download sessions

**Correlation Keys:**
1. `client_ip` - Must match
2. `timestamp` - Stream session end overlaps with access.log requests
3. `upstream_host` - Can help identify service

**Matching Logic:**
```
Stream session: IP=172.16.1.143, end=07:50:58, duration=77.59s, host=cp601.prod.do.dsp.mp.microsoft.com
                → Session started at 07:49:40

Access log requests in that window:
  07:49:45 - GET /some/path → belongs to this session
  07:50:10 - GET /another/path → belongs to this session
  07:50:55 - GET /final/path → belongs to this session
```

**Implementation approach:**
```rust
// For each stream entry, find overlapping access.log entries
fn correlate_stream_to_access(
    stream_entry: &StreamLogEntry,
    access_entries: &[LogEntry]
) -> Vec<&LogEntry> {
    let session_start = stream_entry.session_start();
    let session_end = stream_entry.timestamp;

    access_entries.iter()
        .filter(|access| {
            access.client_ip == stream_entry.client_ip &&
            access.timestamp >= session_start &&
            access.timestamp <= session_end &&
            // Host matching (extract domain from access URL)
            access.matches_host(&stream_entry.upstream_host)
        })
        .collect()
}
```

### Phase 3: Store Speed Data

**Option A: Add to Download model**
```csharp
public class Download
{
    // Existing fields...

    // New speed fields from stream-access.log
    public double? DownloadSpeedBps { get; set; }  // bytes/sec
    public double? UploadSpeedBps { get; set; }
    public double? SessionDurationSeconds { get; set; }
}
```

**Option B: Separate StreamSession table**
```csharp
public class StreamSession
{
    public int Id { get; set; }
    public string ClientIp { get; set; }
    public DateTime SessionStart { get; set; }
    public DateTime SessionEnd { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public double DurationSeconds { get; set; }
    public string UpstreamHost { get; set; }

    // Calculated
    public double DownloadSpeedBps => BytesSent / DurationSeconds;
    public double UploadSpeedBps => BytesReceived / DurationSeconds;

    // Foreign key to Download (if correlated)
    public int? DownloadId { get; set; }
}
```

### Phase 4: Display in UI

**Downloads tab enhancement:**
```
┌─────────────────────────────────────────────────────────────┐
│ Counter-Strike 2                                    Steam   │
│ 8.3 GB total                                               │
│ ↓ 7.96 GB (cache hit)  @ 125.4 MB/s avg                   │
│ ↑ 339.88 MB (cache miss) @ 45.2 MB/s avg                  │
│ Duration: 1m 45s                                           │
└─────────────────────────────────────────────────────────────┘
```

### Phase 5: Multi-File Support

**Log rotation pattern:**
```
logs/
├── stream-access.log      (current)
├── stream-access.log.1    (previous)
├── stream-access.log.2
├── stream-access.log.3
└── stream-access.log.4
```

**Processing order:** Oldest first (`.4` → `.3` → `.2` → `.1` → current)

**Same pattern as access.log** - use existing glob/discovery logic:
```rust
fn discover_stream_logs(log_dir: &Path) -> Vec<PathBuf> {
    let mut logs = vec![];

    // Find all stream-access.log* files
    for entry in fs::read_dir(log_dir).ok()? {
        let path = entry.ok()?.path();
        let name = path.file_name()?.to_str()?;
        if name.starts_with("stream-access.log") {
            logs.push(path);
        }
    }

    // Sort: .4, .3, .2, .1, then current (no suffix)
    logs.sort_by(|a, b| {
        let a_num = extract_rotation_number(a);
        let b_num = extract_rotation_number(b);
        b_num.cmp(&a_num) // Descending, so higher numbers first
    });

    logs
}
```

---

## Correlation Challenges

### Challenge 1: Host Matching

Stream log has: `"cp601.prod.do.dsp.mp.microsoft.com"`
Access log has: Full URL like `GET http://tlu.dl.delivery.mp.microsoft.com/...`

**Solution:** Extract domain from access URL and match patterns:
```rust
fn hosts_match(stream_host: &str, access_url: &str) -> bool {
    // Extract domain from access URL
    let access_domain = extract_domain(access_url);

    // Check if they're for the same service
    // e.g., both are *.microsoft.com or both are steam CDN
    same_service(stream_host, access_domain)
}
```

### Challenge 2: Multiple Concurrent Sessions

A client might have multiple simultaneous downloads:
- Steam game + Windows Update at same time
- Same IP, overlapping time windows

**Solution:** Group by upstream_host to separate sessions:
```rust
// Group stream entries by (client_ip, upstream_host_pattern)
let sessions = stream_entries
    .group_by(|e| (e.client_ip.clone(), service_from_host(&e.upstream_host)));
```

### Challenge 3: Session vs Request Granularity

- Stream log = one entry per TCP session (can contain many HTTP requests)
- Access log = one entry per HTTP request

**Solution:** Aggregate access log requests within a stream session to match totals

---

## API Endpoints

```
GET /api/stats/bandwidth
{
    "currentDownloadSpeed": 52428800,  // bytes/sec (from active sessions)
    "currentUploadSpeed": 1048576,
    "averageDownloadSpeed": 45000000,  // 7-day average
    "peakDownloadSpeed": 125000000
}

GET /api/downloads/{id}/speed
{
    "downloadId": 123,
    "averageDownloadSpeed": 125400000,
    "averageUploadSpeed": 45200000,
    "sessionDuration": 105.5,
    "streamSessions": [
        {
            "start": "2025-12-20T07:49:40Z",
            "end": "2025-12-20T07:50:58Z",
            "bytesSent": 283399,
            "bytesReceived": 91106,
            "downloadSpeedBps": 18261,
            "upstreamHost": "cp601.prod.do.dsp.mp.microsoft.com"
        }
    ]
}
```

---

## Prometheus Metrics

```
# Per-session speed (recent)
lancache_session_download_speed_bytes_per_second
lancache_session_upload_speed_bytes_per_second

# Aggregated
lancache_average_download_speed_bytes_per_second
lancache_peak_download_speed_bytes_per_second
```

---

## Where Real-Time Bandwidth Data Comes From

### Source 1: Network Interface Statistics (Recommended)

Linux exposes real-time byte counters at:
- `/proc/net/dev` - all interfaces with rx_bytes/tx_bytes totals
- `/sys/class/net/<interface>/statistics/rx_bytes` and `tx_bytes`

**How it works:**
```
Time T1: rx_bytes = 1,000,000
Time T2 (1 sec later): rx_bytes = 1,500,000
Download speed = 500,000 bytes/sec = 500 KB/s
```

This is how tools like `nload`, `bmon`, `iftop`, and Node Exporter work.

### Source 2: Docker Container Metrics

- **cAdvisor**: Exposes `container_network_receive_bytes_total` and `container_network_transmit_bytes_total` per container
- **Docker API**: `/containers/{id}/stats` endpoint provides real-time network I/O

### Source 3: NGINX Module (Complex)

- **traffic-accounting-nginx-module**: Real-time bytes_in/bytes_out but requires recompiling nginx
- **NGINX Plus**: Commercial, has real-time bytes via API
- **stub_status**: Only provides connection counts, NOT bytes (useless for bandwidth)

---

## Implementation Options

### Option A: Network Interface Monitoring (Simplest)

**How it works:**
1. Identify which network interface lancache uses (e.g., `eth0`, `br-lancache`)
2. Poll `/sys/class/net/{interface}/statistics/rx_bytes` and `tx_bytes` every 1-2 seconds
3. Calculate delta to get bytes/second
4. Expose via API and Prometheus metrics

**Pros:**
- No changes to lancache/nginx
- Works with existing setup
- Very lightweight

**Cons:**
- Measures ALL traffic on that interface, not just lancache
- If lancache shares network with other services, numbers won't be exact

**Implementation in lancache-manager:**
```csharp
// BandwidthMonitorService.cs
public class BandwidthMonitorService : BackgroundService
{
    private long _lastRxBytes = 0;
    private long _lastTxBytes = 0;
    private DateTime _lastCheck = DateTime.UtcNow;

    public double DownloadBytesPerSecond { get; private set; }
    public double UploadBytesPerSecond { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var (rxBytes, txBytes) = ReadNetworkStats();
            var elapsed = (DateTime.UtcNow - _lastCheck).TotalSeconds;

            if (_lastRxBytes > 0 && elapsed > 0)
            {
                DownloadBytesPerSecond = (rxBytes - _lastRxBytes) / elapsed;
                UploadBytesPerSecond = (txBytes - _lastTxBytes) / elapsed;
            }

            _lastRxBytes = rxBytes;
            _lastTxBytes = txBytes;
            _lastCheck = DateTime.UtcNow;

            await Task.Delay(1000, stoppingToken);
        }
    }

    private (long rx, long tx) ReadNetworkStats()
    {
        // Read from /sys/class/net/{interface}/statistics/
        // Or call Docker API for container stats
    }
}
```

---

### Option B: Use Existing Tools (Prometheus Stack)

**Add to docker-compose:**
```yaml
  node-exporter:
    image: quay.io/prometheus/node-exporter:latest
    container_name: node-exporter
    network_mode: host
    pid: host
    volumes:
      - '/:/host:ro,rslave'
    command:
      - '--path.rootfs=/host'
    restart: unless-stopped

  cadvisor:
    image: gcr.io/cadvisor/cadvisor:latest
    container_name: cadvisor
    volumes:
      - /:/rootfs:ro
      - /var/run:/var/run:ro
      - /sys:/sys:ro
      - /var/lib/docker/:/var/lib/docker:ro
    restart: unless-stopped
```

**Prometheus queries for bandwidth:**
```promql
# Network receive rate (bytes/sec)
rate(node_network_receive_bytes_total{device="eth0"}[1m])

# Network transmit rate (bytes/sec)
rate(node_network_transmit_bytes_total{device="eth0"}[1m])

# Per-container network (from cAdvisor)
rate(container_network_receive_bytes_total{name="lancache"}[1m])
```

**Pros:**
- Industry standard
- Works with Grafana dashboards
- Already have Prometheus metrics endpoint in lancache-manager

**Cons:**
- Requires additional containers
- More complex setup for users

---

### Option C: Netdata (Zero-Config Alternative)

**Add to docker-compose:**
```yaml
  netdata:
    image: netdata/netdata
    container_name: netdata
    hostname: lancache-host
    cap_add:
      - SYS_PTRACE
    security_opt:
      - apparmor:unconfined
    volumes:
      - /proc:/host/proc:ro
      - /sys:/host/sys:ro
      - /var/run/docker.sock:/var/run/docker.sock:ro
    ports:
      - 19999:19999
    restart: unless-stopped
```

**Pros:**
- Automatic per-second monitoring
- Beautiful built-in dashboards
- Zero configuration
- Shows per-container and per-interface bandwidth

**Cons:**
- Separate UI (not integrated into lancache-manager)
- Resource usage (~100MB RAM)

---

### Option D: Integrate Into Lancache-Manager (Full Solution)

**Phase 1: Add bandwidth monitoring service**
1. Create `BandwidthMonitorService` that reads network stats
2. Store current speeds in memory (no database needed for real-time)
3. Add API endpoint: `GET /api/stats/bandwidth`
   ```json
   {
     "downloadBytesPerSecond": 52428800,
     "uploadBytesPerSecond": 1048576,
     "downloadFormatted": "50 MB/s",
     "uploadFormatted": "1 MB/s",
     "interface": "eth0"
   }
   ```

**Phase 2: Add Prometheus metrics**
```
lancache_network_receive_bytes_per_second
lancache_network_transmit_bytes_per_second
```

**Phase 3: Add to dashboard UI**
- Real-time speed gauge/chart
- Historical bandwidth graph (store samples in DB if needed)

**Configuration needed:**
```json
{
  "Monitoring": {
    "NetworkInterface": "eth0",
    "BandwidthSampleInterval": 1000
  }
}
```

---

## Recommended Approach

### For Lancache-Manager Integration:

1. **Option A (Network Interface)** for a simple built-in solution
   - Adds minimal complexity
   - Works without modifying lancache container
   - Good for showing in dashboard

2. **Document Option B (Prometheus/Grafana)** for advanced users
   - Provide docker-compose snippet
   - Include sample Grafana dashboard JSON
   - Users who want detailed metrics can opt-in

### What We Can Store:

| Data | Storage | Notes |
|------|---------|-------|
| Current speed | Memory only | Real-time, no persistence needed |
| Peak speed | Database | Store daily/hourly peaks |
| Speed history | Database (optional) | 1-minute samples for charts |

---

## Technical Considerations

### Docker Networking Modes

- **Host mode**: Network interface = host interface (easy to monitor)
- **Bridge mode**: Each container has virtual interface (need to find correct one)
- **Lancache typically uses host mode** for performance

### Multi-Interface Systems

May need configuration to specify which interface to monitor, or auto-detect the one with most traffic.

### Windows vs Linux

- Linux: `/proc/net/dev`, `/sys/class/net/`
- Windows: Performance counters, WMI
- Docker Desktop: Limited access to host networking

---

---

## btop / glances / htop - What They Provide

These tools (btop, glances, htop) show **system-wide** network bandwidth:

| Tool | Network Metrics | Per-Process? | API/Export? |
|------|-----------------|--------------|-------------|
| btop | ✅ TX/RX bytes/sec per interface | ❌ No | ❌ No |
| glances | ✅ TX/RX bytes/sec per interface | ✅ Yes (optional) | ✅ REST API, Prometheus |
| htop | ❌ CPU/Memory only | N/A | ❌ No |

### glances - Best Option for Integration

**glances** has a built-in REST API and Prometheus exporter:

```bash
# Run with web server
glances -w

# Access API
curl http://localhost:61208/api/3/network
# Returns: [{"interface_name": "eth0", "bytes_recv": 123456, "bytes_sent": 654321, ...}]

# Run with Prometheus exporter
glances --export prometheus
```

This could be scraped by lancache-manager to show real-time network speed!

---

## Recommendation Summary

### For Per-File Transfer Speed (Already Possible):
1. Check if your lancache log includes `request_time`
2. If yes: Parse it and calculate `bytes / request_time` per request
3. Aggregate by active download session

### For Real-Time Aggregate Network Speed:
1. **Option A**: Add glances to docker-compose, scrape its API
2. **Option B**: Monitor `/proc/net/dev` directly from lancache-manager
3. **Option C**: Use Node Exporter + Prometheus (already have metrics endpoint)

### For Real-Time Per-Game Speed (Approximation):
1. Track rate of bytes received for each active download session
2. Calculate rolling average: bytes received in last N seconds / N
3. This is what your current `_currentBytesPerSecond` metric approximates

---

## Sources

- [Node Exporter Network Metrics](https://www.robustperception.io/network-interface-metrics-from-the-node-exporter/)
- [Prometheus cAdvisor Guide](https://prometheus.io/docs/guides/cadvisor/)
- [Linux Kernel Network Statistics](https://www.kernel.org/doc/html/latest/networking/statistics.html)
- [Traffic Accounting NGINX Module](https://github.com/Lax/traffic-accounting-nginx-module)
- [Netdata Docker Monitoring](https://www.netdata.cloud/blog/docker-monitoring-netdata/)
- [Grafana Node Exporter Setup](https://grafana.com/docs/grafana-cloud/send-data/metrics/metrics-prometheus/prometheus-config-examples/docker-compose-linux/)
- [18 Commands to Monitor Network Bandwidth](https://www.binarytides.com/linux-commands-monitor-network/)
- [NGINX Metrics Collection](https://www.datadoghq.com/blog/how-to-collect-nginx-metrics/)
- [LanCache Monolithic Docs](https://lancache.net/docs/containers/monolithic/)
- [Lancache Docker with request_time](https://github.com/miquella/lancache-docker/blob/master/etc/nginx/nginx.conf)
- [Glances REST API](https://glances.readthedocs.io/en/latest/api.html)

---

## Next Steps (Updated)

### Priority 1: Parse stream-access.log
1. Add `StreamLogEntry` struct to Rust processor
2. Create `stream_parser.rs` with regex for the format
3. Support rotated files: `stream-access.log.1`, `.2`, etc.

### Priority 2: Correlate with Access Log
1. Build correlation logic (IP + time window + service)
2. Handle edge cases (concurrent downloads, different services)
3. Store correlation in database

### Priority 3: Update Database Schema
1. Add speed fields to Download model OR create StreamSession table
2. Run migration
3. Update Rust processor to write speed data

### Priority 4: Display in UI
1. Add speed column to Downloads table
2. Show `@ X MB/s` next to byte totals
3. Add to dashboard metrics

### Priority 5: Prometheus/Grafana
1. Expose `lancache_session_download_speed_bytes_per_second`
2. Add to existing metrics service
3. Update Grafana dashboard templates

---

## Questions to Resolve

1. **Storage approach:** Add fields to Download or separate StreamSession table?
2. **Correlation accuracy:** How precise do we need IP+time+host matching to be?
3. **Real-time vs historical:** Do we need live speed for active downloads, or just completed?
4. **Multi-datasource:** Should stream logs be per-datasource like access logs?
