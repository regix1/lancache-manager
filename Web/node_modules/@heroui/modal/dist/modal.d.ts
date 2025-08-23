import * as _heroui_system from '@heroui/system';
import { ReactNode } from 'react';
import { UseModalProps } from './use-modal.js';
import 'tailwind-variants';
import '@heroui/theme';
import 'framer-motion';
import '@react-aria/overlays';
import '@heroui/react-utils';
import '@react-stately/overlays';

interface ModalProps extends UseModalProps {
    /**
     * The content of the modal. Usually the ModalContent
     */
    children: ReactNode;
}
declare const Modal: _heroui_system.InternalForwardRefRenderFunction<"div", ModalProps, never>;

export { type ModalProps, Modal as default };
