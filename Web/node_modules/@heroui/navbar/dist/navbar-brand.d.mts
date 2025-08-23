import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';

interface NavbarBrandProps extends HTMLHeroUIProps<"div"> {
    children?: React.ReactNode | React.ReactNode[];
}
declare const NavbarBrand: _heroui_system.InternalForwardRefRenderFunction<"div", NavbarBrandProps, never>;

export { type NavbarBrandProps, NavbarBrand as default };
