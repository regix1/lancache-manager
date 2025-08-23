import * as _heroui_system from '@heroui/system';
import { UseAccordionItemProps } from './use-accordion-item.js';
import 'framer-motion';
import './base/accordion-item-base.js';
import '@heroui/theme';
import '@heroui/aria-utils';
import '@react-types/shared';
import 'react';
import 'tailwind-variants';
import '@heroui/react-utils';
import '@react-stately/tree';

interface AccordionItemProps extends UseAccordionItemProps {
}
declare const AccordionItem: _heroui_system.InternalForwardRefRenderFunction<"button", AccordionItemProps, never>;

export { type AccordionItemProps, AccordionItem as default };
