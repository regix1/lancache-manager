import * as _heroui_system from '@heroui/system';
import * as react_jsx_runtime from 'react/jsx-runtime';
import * as _heroui_shared_icons from '@heroui/shared-icons';
import { UseAlertProps } from './use-alert.mjs';
import 'react';
import '@heroui/button';
import '@heroui/theme';
import '@heroui/react-utils';

declare const iconMap: {
    readonly primary: (props: _heroui_shared_icons.IconSvgProps & {
        className?: string;
    }) => react_jsx_runtime.JSX.Element;
    readonly secondary: (props: _heroui_shared_icons.IconSvgProps & {
        className?: string;
    }) => react_jsx_runtime.JSX.Element;
    readonly success: (props: _heroui_shared_icons.IconSvgProps & {
        className?: string;
    }) => react_jsx_runtime.JSX.Element;
    readonly warning: (props: _heroui_shared_icons.IconSvgProps & {
        className?: string;
    }) => react_jsx_runtime.JSX.Element;
    readonly danger: (props: _heroui_shared_icons.IconSvgProps & {
        className?: string;
    }) => react_jsx_runtime.JSX.Element;
};
type AlertColor = keyof typeof iconMap;
interface AlertProps extends Omit<UseAlertProps, "hasContent"> {
}
declare const Alert: _heroui_system.InternalForwardRefRenderFunction<"div", AlertProps, never>;

export { type AlertColor, type AlertProps, Alert as default };
