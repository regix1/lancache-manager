import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';

interface NavbarMenuItemProps extends HTMLHeroUIProps<"li"> {
    /**
     * Whether the item is active or not.
     * @default false
     */
    isActive?: boolean;
    children?: React.ReactNode;
}
declare const NavbarMenuItem: _heroui_system.InternalForwardRefRenderFunction<"li", NavbarMenuItemProps, never>;

export { type NavbarMenuItemProps, NavbarMenuItem as default };
