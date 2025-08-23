import * as _heroui_system_rsc from '@heroui/system-rsc';
import { UseKbdProps } from './use-kbd.js';
import './utils.js';
import 'react';
import 'tailwind-variants';
import '@heroui/theme';
import '@heroui/react-utils';

interface KbdProps extends UseKbdProps {
}
declare const Kbd: _heroui_system_rsc.InternalForwardRefRenderFunction<"kbd", KbdProps, never>;

export { type KbdProps, Kbd as default };
