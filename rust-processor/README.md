# Lancache Manager - Rust Log Processor

A high-performance log processor written in Rust for processing Lancache access logs and storing them in SQLite database.

## Features

- **5-10x faster** than C# implementation
- Processes 100,000 entries per transaction for maximum throughput
- Session-based download grouping with 5-minute timeout
- Regex-based log parsing matching C# patterns exactly
- Depot ID extraction for Steam downloads
- Real-time progress tracking via JSON file
- Memory-efficient streaming processing

## Architecture

### Modules

- **main.rs**: Entry point, orchestrates processing pipeline
- **models.rs**: Data structures for LogEntry
- **parser.rs**: Regex-based log parsing with depot extraction
- **session.rs**: Session tracking with configurable timeout

### Database Operations

The processor performs the following operations:

1. **Download Sessions**: Groups log entries by client_ip + service with 5-minute session timeout
2. **ClientStats**: Aggregates per-client statistics (bytes, downloads, last seen)
3. **ServiceStats**: Aggregates per-service statistics (bytes, downloads, last activity)
4. **LogEntries**: Inserts all parsed entries with DownloadId association

### SQLite Optimizations

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA cache_size = 1000000;  -- 1GB cache
PRAGMA locking_mode = EXCLUSIVE;
PRAGMA temp_store = MEMORY;
```

## Building

### Prerequisites

- Rust 1.70+ with cargo
- Windows/Linux

### Build Release Binary

```bash
cd rust-processor
cargo build --release
```

The compiled binary will be at `target/release/lancache_processor.exe` (Windows) or `target/release/lancache_processor` (Linux).

### Copy to API Directory

For integration with the C# API, copy the binary to:

```
Api/LancacheManager/rust-processor/lancache_processor.exe
```

## Usage

### Command Line

```bash
lancache_processor.exe <db_path> <log_path> <progress_path> <start_position>
```

**Arguments:**
- `db_path`: Path to SQLite database (e.g., `H:/data/LancacheManager.db`)
- `log_path`: Path to access.log file (e.g., `C:/lancache/logs/access.log`)
- `progress_path`: Path to write progress JSON (e.g., `H:/data/rust_progress.json`)
- `start_position`: Line number to start from (0 = from beginning)

**Example:**

```bash
lancache_processor.exe "H:/data/LancacheManager.db" "C:/lancache/logs/access.log" "H:/data/rust_progress.json" 0
```

### Via C# API

The Rust processor is integrated with the C# API via `RustLogProcessorService`:

**Start Processing:**
```http
POST /api/management/process-logs-rust
Authorization: Bearer <api_key>
```

**Cancel Processing:**
```http
POST /api/management/process-logs-rust/cancel
Authorization: Bearer <api_key>
```

**Check Status:**
```http
GET /api/management/process-logs-rust/status
```

### Progress Tracking

The processor writes progress to a JSON file specified by `progress_path`:

```json
{
  "total_lines": 4000000,
  "lines_parsed": 2500000,
  "entries_saved": 2450000,
  "percent_complete": 62.5,
  "status": "processing",
  "message": "2500000 lines parsed, 2450000 entries saved",
  "timestamp": "2025-01-29T14:30:45Z"
}
```

The C# service polls this file every second and sends updates via SignalR to connected clients.

## Performance

### Benchmarks

Processing 4 million log entries:

| Implementation | Time | Throughput |
|----------------|------|------------|
| C# (EF Core) | ~25 minutes | ~2,666 lines/sec |
| Rust (rusqlite) | ~3-5 minutes | ~13,333-22,222 lines/sec |

**Speedup: 5-8x faster**

### Optimizations

1. **Bulk Transactions**: 100k inserts per transaction
2. **Prepared Statements**: Cached for repeated use
3. **Memory Buffering**: 8MB read buffer
4. **Zero-Copy Parsing**: Regex captures without allocations
5. **SQLite PRAGMAs**: WAL mode, 1GB cache, exclusive locking

## Database Schema Compatibility

The Rust processor is fully compatible with the existing C# schema:

### Downloads Table
- Id (INTEGER PRIMARY KEY)
- Service (TEXT)
- ClientIp (TEXT)
- StartTime (DATETIME)
- EndTime (DATETIME)
- CacheHitBytes (INTEGER)
- CacheMissBytes (INTEGER)
- IsActive (BOOLEAN)
- LastUrl (TEXT)
- DepotId (INTEGER, nullable)
- GameAppId (INTEGER, nullable)
- GameName (TEXT, nullable)
- GameImageUrl (TEXT, nullable)

### LogEntries Table
- Id (INTEGER PRIMARY KEY)
- Timestamp (DATETIME)
- ClientIp (TEXT)
- Service (TEXT)
- Method (TEXT, always "GET")
- Url (TEXT)
- StatusCode (INTEGER)
- BytesServed (INTEGER)
- CacheStatus (TEXT: "HIT", "MISS", "UNKNOWN")
- DepotId (INTEGER, nullable)
- DownloadId (INTEGER, FK to Downloads)
- CreatedAt (DATETIME)

### ClientStats Table
- ClientIp (TEXT PRIMARY KEY)
- TotalCacheHitBytes (INTEGER)
- TotalCacheMissBytes (INTEGER)
- LastSeen (DATETIME)
- TotalDownloads (INTEGER)

### ServiceStats Table
- Service (TEXT PRIMARY KEY)
- TotalCacheHitBytes (INTEGER)
- TotalCacheMissBytes (INTEGER)
- LastActivity (DATETIME)
- TotalDownloads (INTEGER)

## Session Grouping Logic

Downloads are grouped into sessions using the following logic:

1. **Session Key**: `{client_ip}_{service}`
2. **Session Timeout**: 5 minutes of inactivity
3. **New Session Triggers**:
   - First log entry for a client+service combination
   - Gap > 5 minutes since last entry for that client+service
4. **Session Continuation**: Entries within 5 minutes are grouped together

This matches the C# implementation exactly (`ProcessingConstants.SessionGapTimeout`).

## Log Parsing

### Supported Formats

The parser supports the same regex pattern as the C# implementation:

```regex
^(?:\[(?P<service>[^\]]+)\]\s+)?(?P<ip>\S+)\s+[^\[]*\[(?P<time>[^\]]+)\]\s+"(?P<method>[A-Z]+)\s+(?P<url>\S+)(?:\s+HTTP/(?P<httpVersion>[^"\s]+))?"\s+(?P<status>\d{3})\s+(?P<bytes>-|\d+)(?P<rest>.*)$
```

### Timestamp Formats

- `dd/MMM/yyyy:HH:mm:ss` (e.g., `24/Jan/2025:14:30:45`)
- `yyyy-MM-dd HH:mm:ss` (e.g., `2025-01-24 14:30:45`)
- `yyyy-MM-ddTHH:mm:ss` (e.g., `2025-01-24T14:30:45`)

### Depot Extraction

For Steam service, depot IDs are extracted from URLs matching `/depot/(\d+)/`.

### Cache Status

Cache status ("HIT", "MISS") is extracted from the 3rd quoted field in the log line's "rest" portion.

## Error Handling

- Unparseable lines are skipped (logged to stderr)
- Database errors abort the transaction and exit with non-zero code
- Progress file write failures are logged but don't abort processing

## Testing

Run unit tests:

```bash
cargo test
```

Run with sample data:

```bash
cargo run --release -- test.db sample.log progress.json 0
```

## Integration Notes

- The C# API spawns the Rust process and monitors it
- Progress updates are sent via SignalR to connected clients
- Final position is saved to `state.json` via `StateService`
- Rust process runs completely independently from C# services

## Future Enhancements

- [ ] Parallel processing with Rayon for multi-core systems
- [ ] Incremental depot mapping during processing
- [ ] Compressed log file support (.gz)
- [ ] Real-time streaming mode (process as logs are written)
- [ ] Checkpoint/resume capability for very large files