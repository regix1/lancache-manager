import * as _heroui_system from '@heroui/system';
import { UseNavbarProps } from './use-navbar.mjs';
import 'react';
import 'tailwind-variants';
import '@heroui/theme';
import '@heroui/react-utils';
import 'framer-motion';

interface NavbarProps extends Omit<UseNavbarProps, "hideOnScroll"> {
    children?: React.ReactNode | React.ReactNode[];
}
declare const Navbar: _heroui_system.InternalForwardRefRenderFunction<"div", NavbarProps, never>;

export { type NavbarProps, Navbar as default };
