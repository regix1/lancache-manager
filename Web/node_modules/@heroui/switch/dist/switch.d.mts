import * as _heroui_system from '@heroui/system';
import { UseSwitchProps } from './use-switch.mjs';
import 'react';
import 'tailwind-variants';
import '@heroui/theme';
import '@react-aria/switch';

interface SwitchProps extends UseSwitchProps {
}
declare const Switch: _heroui_system.InternalForwardRefRenderFunction<"input", SwitchProps, never>;

export { type SwitchProps, Switch as default };
