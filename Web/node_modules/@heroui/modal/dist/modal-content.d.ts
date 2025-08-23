import * as react_jsx_runtime from 'react/jsx-runtime';
import { AriaDialogProps } from '@react-aria/dialog';
import { ReactNode } from 'react';
import { HTMLHeroUIProps } from '@heroui/system';

type KeysToOmit = "children" | "role";
interface ModalContentProps extends AriaDialogProps, HTMLHeroUIProps<"div", KeysToOmit> {
    children: ReactNode | ((onClose: () => void) => ReactNode);
}
declare const ModalContent: {
    (props: ModalContentProps): react_jsx_runtime.JSX.Element;
    displayName: string;
};

export { type ModalContentProps, ModalContent as default };
