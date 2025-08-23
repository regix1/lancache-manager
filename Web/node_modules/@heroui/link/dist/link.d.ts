import * as _heroui_system from '@heroui/system';
import { UseLinkProps } from './use-link.js';
import 'react';
import '@react-types/link';
import '@heroui/theme';
import '@heroui/react-utils';

interface LinkProps extends UseLinkProps {
}
declare const Link: _heroui_system.InternalForwardRefRenderFunction<"a", LinkProps, never>;

export { type LinkProps, Link as default };
