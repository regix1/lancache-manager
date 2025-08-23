import { HeroUIPluginConfig } from './types.mjs';
import plugin from 'tailwindcss/plugin.js';
import './colors/types.mjs';

/**
 * Based on tw-colors by L-Blondy
 * @see https://github.com/L-Blondy/tw-colors
 */

declare const heroui: (config?: HeroUIPluginConfig) => ReturnType<typeof plugin>;

export { heroui };
