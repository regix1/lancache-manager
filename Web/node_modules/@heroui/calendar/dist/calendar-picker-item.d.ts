import * as react from 'react';
import * as _react_types_button from '@react-types/button';
import * as _heroui_system from '@heroui/system';

declare const CalendarPickerItem: react.ForwardRefExoticComponent<Omit<_heroui_system.PropsOf<"button">, "slot" | "ref" | "color" | "size" | "defaultChecked" | "defaultValue"> & {
    as?: _heroui_system.As;
} & _react_types_button.AriaButtonProps<"button"> & {
    allowTextSelectionOnPress?: boolean;
    role?: string;
} & react.RefAttributes<HTMLButtonElement>>;

export { CalendarPickerItem };
