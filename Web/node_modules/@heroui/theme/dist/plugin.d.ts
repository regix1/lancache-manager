import { HeroUIPluginConfig } from './types.js';
import plugin from 'tailwindcss/plugin.js';
import './colors/types.js';

/**
 * Based on tw-colors by L-Blondy
 * @see https://github.com/L-Blondy/tw-colors
 */

declare const heroui: (config?: HeroUIPluginConfig) => ReturnType<typeof plugin>;

export { heroui };
