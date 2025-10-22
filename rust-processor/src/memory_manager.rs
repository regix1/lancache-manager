use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

/// Memory manager that monitors and limits memory usage
/// When memory exceeds the limit, it triggers cleanup callbacks
pub struct MemoryManager {
    max_memory_mb: u64,
    current_memory_mb: Arc<AtomicU64>,
    cleanup_callbacks: Vec<Box<dyn Fn() + Send + Sync>>,
}

impl MemoryManager {
    /// Create a new memory manager with a maximum memory limit in MB
    /// If max_memory_mb is 0, memory limiting is disabled
    pub fn new(max_memory_mb: u64) -> Self {
        Self {
            max_memory_mb,
            current_memory_mb: Arc::new(AtomicU64::new(0)),
            cleanup_callbacks: Vec::new(),
        }
    }

    /// Get the current memory usage in MB
    #[cfg(target_os = "linux")]
    pub fn get_current_memory_mb() -> u64 {
        // Read from /proc/self/status on Linux
        if let Ok(status) = std::fs::read_to_string("/proc/self/status") {
            for line in status.lines() {
                if line.starts_with("VmRSS:") {
                    // VmRSS is the actual physical memory in use
                    let parts: Vec<&str> = line.split_whitespace().collect();
                    if parts.len() >= 2 {
                        if let Ok(kb) = parts[1].parse::<u64>() {
                            return kb / 1024; // Convert KB to MB
                        }
                    }
                }
            }
        }
        0
    }

    #[cfg(target_os = "windows")]
    pub fn get_current_memory_mb() -> u64 {
        use std::mem;

        #[repr(C)]
        struct ProcessMemoryCountersEx {
            cb: u32,
            page_fault_count: u32,
            peak_working_set_size: usize,
            working_set_size: usize,
            quota_peak_paged_pool_usage: usize,
            quota_paged_pool_usage: usize,
            quota_peak_non_paged_pool_usage: usize,
            quota_non_paged_pool_usage: usize,
            pagefile_usage: usize,
            peak_pagefile_usage: usize,
            private_usage: usize,
        }

        extern "system" {
            fn GetCurrentProcess() -> *mut std::ffi::c_void;
            fn K32GetProcessMemoryInfo(
                process: *mut std::ffi::c_void,
                pmc: *mut ProcessMemoryCountersEx,
                cb: u32,
            ) -> i32;
        }

        unsafe {
            let mut pmc: ProcessMemoryCountersEx = mem::zeroed();
            pmc.cb = mem::size_of::<ProcessMemoryCountersEx>() as u32;

            let process = GetCurrentProcess();
            if K32GetProcessMemoryInfo(process, &mut pmc, pmc.cb) != 0 {
                // Return working set size in MB
                return (pmc.working_set_size / (1024 * 1024)) as u64;
            }
        }
        0
    }

    #[cfg(not(any(target_os = "linux", target_os = "windows")))]
    pub fn get_current_memory_mb() -> u64 {
        // Fallback for unsupported platforms - just return 0
        0
    }

    /// Check if memory usage is within limits
    /// Returns true if within limits, false if exceeded
    pub fn check_memory(&self) -> bool {
        if self.max_memory_mb == 0 {
            return true; // Memory limiting disabled
        }

        let current = Self::get_current_memory_mb();
        self.current_memory_mb.store(current, Ordering::Relaxed);

        current <= self.max_memory_mb
    }

    /// Force memory cleanup by triggering all registered callbacks
    pub fn force_cleanup(&self) {
        println!("Memory limit exceeded ({} MB / {} MB max) - forcing cleanup",
                 self.current_memory_mb.load(Ordering::Relaxed),
                 self.max_memory_mb);

        for callback in &self.cleanup_callbacks {
            callback();
        }

        // Force garbage collection if available on the platform
        Self::force_gc();
    }

    /// Force garbage collection / memory release
    #[cfg(target_os = "linux")]
    fn force_gc() {
        use libc::{malloc_trim, size_t};
        unsafe {
            // malloc_trim releases unused memory back to the OS
            malloc_trim(0 as size_t);
        }
    }

    #[cfg(not(target_os = "linux"))]
    fn force_gc() {
        // On Windows, memory is automatically released when freed
        // No explicit GC needed
    }

    /// Register a cleanup callback that will be called when memory limit is exceeded
    #[allow(dead_code)]
    pub fn register_cleanup_callback<F>(&mut self, callback: F)
    where
        F: Fn() + Send + Sync + 'static,
    {
        self.cleanup_callbacks.push(Box::new(callback));
    }

    /// Get the current memory usage percentage
    #[allow(dead_code)]
    pub fn get_memory_usage_percent(&self) -> f64 {
        if self.max_memory_mb == 0 {
            return 0.0;
        }
        let current = self.current_memory_mb.load(Ordering::Relaxed);
        (current as f64 / self.max_memory_mb as f64) * 100.0
    }

    /// Start a background monitoring thread that periodically checks memory usage
    #[allow(dead_code)]
    pub fn start_monitoring(self: Arc<Self>, check_interval_ms: u64) -> thread::JoinHandle<()> {
        thread::spawn(move || {
            loop {
                thread::sleep(Duration::from_millis(check_interval_ms));

                if !self.check_memory() {
                    self.force_cleanup();
                }
            }
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_memory_manager_disabled() {
        let manager = MemoryManager::new(0);
        assert!(manager.check_memory());
    }

    #[test]
    fn test_memory_manager_get_current() {
        let current = MemoryManager::get_current_memory_mb();
        // Should be able to get some memory reading (at least 1 MB for the test process)
        assert!(current > 0);
    }

    #[test]
    fn test_memory_usage_percent() {
        let manager = MemoryManager::new(100);
        manager.current_memory_mb.store(50, Ordering::Relaxed);
        assert_eq!(manager.get_memory_usage_percent(), 50.0);
    }
}
