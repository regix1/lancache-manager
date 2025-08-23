import './base/accordion-item-base.js';
import * as _heroui_system from '@heroui/system';
import { UseAccordionProps } from './use-accordion.js';
import './use-accordion-item.js';
import '@heroui/theme';
import '@heroui/aria-utils';
import '@react-types/shared';
import 'react';
import 'framer-motion';
import '@react-types/accordion';
import '@heroui/react-utils';
import '@react-stately/tree';
import '@heroui/divider';
import './accordion-item.js';
import 'tailwind-variants';

interface AccordionProps extends UseAccordionProps {
}
declare const AccordionGroup: _heroui_system.InternalForwardRefRenderFunction<"div", AccordionProps, never>;

export { type AccordionProps, AccordionGroup as default };
