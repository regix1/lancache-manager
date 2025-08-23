import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';
import { ReactRef } from '@heroui/react-utils';

interface ModalHeaderProps extends HTMLHeroUIProps<"header"> {
    /**
     * Ref to the DOM node.
     */
    ref?: ReactRef<HTMLElement | null>;
}
declare const ModalHeader: _heroui_system.InternalForwardRefRenderFunction<"header", ModalHeaderProps, never>;

export { type ModalHeaderProps, ModalHeader as default };
