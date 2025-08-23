import * as react from 'react';
import * as _heroui_system_rsc from '@heroui/system-rsc';
import { HTMLHeroUIProps, PropGetter } from '@heroui/system-rsc';
import { CodeVariantProps } from '@heroui/theme';
import { ReactRef } from '@heroui/react-utils';

interface UseCodeProps extends HTMLHeroUIProps<"code">, CodeVariantProps {
    /**
     * Ref to the DOM node.
     */
    ref?: ReactRef<HTMLElement | null>;
}
declare function useCode(originalProps: UseCodeProps): {
    Component: _heroui_system_rsc.As<any>;
    children: react.ReactNode;
    getCodeProps: PropGetter;
};
type UseCodeReturn = ReturnType<typeof useCode>;

export { type UseCodeProps, type UseCodeReturn, useCode };
