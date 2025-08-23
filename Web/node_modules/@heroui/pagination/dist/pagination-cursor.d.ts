import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';

interface PaginationCursorProps extends HTMLHeroUIProps<"span"> {
    /**
     * The current active page.
     */
    activePage?: number;
}
declare const PaginationCursor: _heroui_system.InternalForwardRefRenderFunction<"span", PaginationCursorProps, never>;

export { type PaginationCursorProps, PaginationCursor as default };
