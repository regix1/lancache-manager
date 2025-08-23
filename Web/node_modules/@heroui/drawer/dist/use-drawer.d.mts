import * as react from 'react';
import { ModalProps } from '@heroui/modal';
import { ReactRef } from '@heroui/react-utils';
import { PropGetter } from '@heroui/system';

interface Props extends Omit<ModalProps, "placement" | "scrollBehavior" | "children"> {
    /**
     * Ref to the DOM node.
     */
    ref?: ReactRef<HTMLElement | null>;
    /**
     * The placement of the drawer.
     */
    placement?: "top" | "right" | "bottom" | "left";
    /**
     * The scroll behavior of the drawer.
     */
    scrollBehavior?: "inside" | "outside";
}
type UseDrawerProps = Props;
declare function useDrawer(originalProps: UseDrawerProps): {
    domRef: react.RefObject<HTMLElement>;
    getModalProps: PropGetter;
};
type UseDrawerReturn = ReturnType<typeof useDrawer>;

export { type UseDrawerProps, type UseDrawerReturn, useDrawer };
