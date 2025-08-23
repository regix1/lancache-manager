import * as _heroui_system from '@heroui/system';
import { UseUserProps } from './use-user.mjs';
import 'react';
import '@heroui/theme';
import 'tailwind-variants';
import '@heroui/avatar';
import '@heroui/react-utils';

interface UserProps extends UseUserProps {
}
declare const User: _heroui_system.InternalForwardRefRenderFunction<"div", UserProps, never>;

export { type UserProps, User as default };
