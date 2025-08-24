/*
  @license
	Rollup.js v4.48.0
	Sat, 23 Aug 2025 06:19:18 GMT - commit 77bb1ae9293a35036cdccfc2ba34faa0fe703b05

	https://github.com/rollup/rollup

	Released under the MIT License.
*/
export { version as VERSION, defineConfig, rollup, watch } from './shared/node-entry.js';
import './shared/parseAst.js';
import '../native.js';
import 'node:path';
import 'path';
import 'node:process';
import 'node:perf_hooks';
import 'node:fs/promises';
