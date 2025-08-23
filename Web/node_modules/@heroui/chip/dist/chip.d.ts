import * as _heroui_system from '@heroui/system';
import { UseChipProps } from './use-chip.js';
import 'react';
import 'tailwind-variants';
import '@heroui/theme';
import '@heroui/react-utils';
import '@react-types/shared';

interface ChipProps extends Omit<UseChipProps, "isOneChar" | "isCloseButtonFocusVisible"> {
}
declare const Chip: _heroui_system.InternalForwardRefRenderFunction<"div", ChipProps, never>;

export { type ChipProps, Chip as default };
