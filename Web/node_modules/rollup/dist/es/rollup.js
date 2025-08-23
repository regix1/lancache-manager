/*
  @license
	Rollup.js v4.47.1
	Thu, 21 Aug 2025 08:43:12 GMT - commit 21d5c5b74ec8c2cddef08a4122c3411e83f3d62f

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
