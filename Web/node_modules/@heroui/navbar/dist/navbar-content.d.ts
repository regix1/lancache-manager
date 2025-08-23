import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';

interface NavbarContentProps extends HTMLHeroUIProps<"ul"> {
    /**
     * The content of the Navbar.Content. It is usually the `NavbarItem`,
     */
    children?: React.ReactNode | React.ReactNode[];
    /**
     * The justify of the content
     * @default start
     */
    justify?: "start" | "end" | "center";
}
declare const NavbarContent: _heroui_system.InternalForwardRefRenderFunction<"ul", NavbarContentProps, never>;

export { type NavbarContentProps, NavbarContent as default };
