import * as _heroui_system from '@heroui/system';
import { ReactNode } from 'react';
import { UsePopoverProps } from './use-popover.js';
import '@heroui/theme';
import 'framer-motion';
import '@heroui/react-utils';
import '@react-stately/overlays';
import '@react-types/overlays';
import '@react-aria/dialog';
import './use-aria-popover.js';
import '@react-aria/overlays';
import '@heroui/aria-utils';

interface PopoverProps extends UsePopoverProps {
    /**
     * The content of the popover. It is usually the `PopoverTrigger`,
     * and `PopoverContent`
     */
    children: ReactNode[];
}
declare const Popover: _heroui_system.InternalForwardRefRenderFunction<"div", PopoverProps, never>;

export { type PopoverProps, Popover as default };
