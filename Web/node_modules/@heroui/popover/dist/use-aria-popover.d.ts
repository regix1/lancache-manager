import { RefObject } from 'react';
import { AriaPopoverProps, AriaOverlayProps, PopoverAria } from '@react-aria/overlays';
import { OverlayPlacement } from '@heroui/aria-utils';
import { OverlayTriggerState } from '@react-stately/overlays';

interface Props {
    /**
     * Whether the element should render an arrow.
     * @default false
     */
    showArrow?: boolean;
    /**
     * The placement of the element with respect to its anchor element.
     * @default 'top'
     */
    placement?: OverlayPlacement;
    /**
     * A ref for the scrollable region within the overlay.
     * @default popoverRef
     */
    scrollRef?: RefObject<HTMLElement>;
    /**
     * List of dependencies to update the position of the popover.
     * @default []
     */
    updatePositionDeps?: any[];
    /**
     * Whether the popover should close on scroll.
     * @default true
     */
    shouldCloseOnScroll?: boolean;
    /**
     * Whether to close the overlay when the user interacts outside it.
     * @default true
     */
    isDismissable?: boolean;
}
type ReactAriaPopoverProps = Props & Omit<AriaPopoverProps, "placement"> & Omit<AriaOverlayProps, "isDismissable">;
/**
 * Provides the behavior and accessibility implementation for a popover component.
 * A popover is an overlay element positioned relative to a trigger.
 */
declare function useReactAriaPopover(props: ReactAriaPopoverProps, state: OverlayTriggerState): PopoverAria;

export { type Props, type ReactAriaPopoverProps, useReactAriaPopover };
