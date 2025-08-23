import * as _heroui_system_rsc from '@heroui/system-rsc';
import { UseDividerProps } from './use-divider.mjs';
import '@heroui/theme';
import 'react';
import './use-separator.mjs';
import '@react-types/shared';

interface DividerProps extends Omit<UseDividerProps, "children"> {
}
declare const Divider: _heroui_system_rsc.InternalForwardRefRenderFunction<"div", DividerProps, never>;

export { type DividerProps, Divider as default };
