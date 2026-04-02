// React 19 has useSyncExternalStore built-in.
// The use-sync-external-store/shim package accesses React internals that changed
// in React 19, causing "Cannot read properties of undefined (reading 'useState')".
// This shim re-exports React's native implementation to bypass the broken package.
// See: https://github.com/facebook/react/issues/29854
export { useSyncExternalStore } from 'react';
