import * as _heroui_system from '@heroui/system';
import { ReactElement, ReactNode, HTMLAttributes } from 'react';
import { ValidationResult, HelpTextProps, GroupDOMAttributes } from '@react-types/shared';

interface DateInputGroupProps extends ValidationResult, HelpTextProps {
    children?: ReactElement | ReactElement[];
    shouldLabelBeOutside?: boolean;
    label?: ReactNode;
    startContent?: React.ReactNode;
    endContent?: React.ReactNode;
    groupProps?: GroupDOMAttributes;
    wrapperProps?: HTMLAttributes<HTMLElement>;
    helperWrapperProps?: HTMLAttributes<HTMLElement>;
    labelProps?: HTMLAttributes<HTMLElement>;
    descriptionProps?: HTMLAttributes<HTMLElement>;
    errorMessageProps?: HTMLAttributes<HTMLElement>;
}
declare const DateInputGroup: _heroui_system.InternalForwardRefRenderFunction<"div", DateInputGroupProps, never>;

export { DateInputGroup, type DateInputGroupProps };
