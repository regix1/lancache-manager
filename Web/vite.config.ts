import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';
import fs from 'fs';

// Read version from VERSION file, fallback to package.json
let version = '0.0.0';
try {
  version = fs.readFileSync(path.resolve(__dirname, '../VERSION'), 'utf-8').trim();
} catch {
  version = JSON.parse(fs.readFileSync(path.resolve(__dirname, 'package.json'), 'utf-8')).version;
}

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(version)
  },
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      '@components': path.resolve(__dirname, './src/components'),
      '@services': path.resolve(__dirname, './src/services'),
      '@utils': path.resolve(__dirname, './src/utils'),
      '@contexts': path.resolve(__dirname, './src/contexts'),
      '@hooks': path.resolve(__dirname, './src/hooks'),
      // React 19 has useSyncExternalStore built-in — redirect the shim package
      // to React's native export. See: https://github.com/facebook/react/issues/29854
      'use-sync-external-store/shim': 'react'
    },
    // Ensure all packages resolve to the same React instance (prevents duplicate React in dev mode)
    dedupe: ['react', 'react-dom']
  },
  optimizeDeps: {
    include: [
      'react',
      'react-dom'
    ]
  },
  server: {
    port: 3000,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true
      },
      '/hubs': {
        target: 'http://localhost:5000',
        ws: true,
        changeOrigin: true
      },
      '/metrics': {
        target: 'http://localhost:5000',
        changeOrigin: true
      },
      '/swagger': {
        target: 'http://localhost:5000',
        changeOrigin: true
      }
    }
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: true,
    chunkSizeWarningLimit: 1000,
    rollupOptions: {
      output: {
        // Add hash to filenames for cache busting
        entryFileNames: 'assets/[name]-[hash].js',
        chunkFileNames: 'assets/[name]-[hash].js',
        assetFileNames: 'assets/[name]-[hash].[ext]',
        manualChunks: (id) => {
          if (id.includes('node_modules')) {
            if (id.includes('chart.js') || id.includes('react-chartjs-2')) return 'charts';
            // Keep React and tightly coupled runtime packages together to avoid
            // cross-chunk circular initialization in production bundles.
            if (
              id.includes('react') ||
              id.includes('scheduler') ||
              id.includes('number-flow')
            ) {
              return 'react-vendor';
            }
            if (id.includes('@microsoft/signalr')) return 'signalr';
            if (id.includes('zod')) return 'vendor';
            return 'vendor';
          }
        }
      }
    }
  }
});