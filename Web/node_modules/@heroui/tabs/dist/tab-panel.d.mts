import * as _heroui_system from '@heroui/system';
import { HTMLHeroUIProps } from '@heroui/system';
import { AriaTabPanelProps } from '@react-aria/tabs';
import { Key } from '@react-types/shared';
import { ValuesType } from './use-tabs.mjs';
import '@heroui/theme';
import '@heroui/react-utils';
import 'react';
import '@react-stately/tabs';
import '@heroui/aria-utils';
import 'framer-motion';

interface Props extends HTMLHeroUIProps<"div"> {
    /**
     * Whether to destroy inactive tab panel when switching tabs.
     * Inactive tab panels are inert and cannot be interacted with.
     */
    destroyInactiveTabPanel: boolean;
    /**
     * The current tab key.
     */
    tabKey: Key;
    /**
     * The tab list state.
     */
    state: ValuesType["state"];
    /**
     * Component slots classes
     */
    slots: ValuesType["slots"];
    /**
     * User custom classnames
     */
    classNames?: ValuesType["classNames"];
}
type TabPanelProps = Props & AriaTabPanelProps;
/**
 * @internal
 */
declare const TabPanel: _heroui_system.InternalForwardRefRenderFunction<"div", TabPanelProps, never>;

export { type TabPanelProps, TabPanel as default };
