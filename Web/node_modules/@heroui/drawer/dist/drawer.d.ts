import '@heroui/modal';
import * as _heroui_system from '@heroui/system';
import { UseDrawerProps } from './use-drawer.js';
import 'react';
import '@heroui/react-utils';

interface DrawerProps extends UseDrawerProps {
    children: React.ReactNode;
}
declare const Drawer: _heroui_system.InternalForwardRefRenderFunction<"div", DrawerProps, never>;

export { type DrawerProps, Drawer as default };
