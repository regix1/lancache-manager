import * as react from 'react';
import { InputHTMLAttributes } from 'react';
import { GroupDOMAttributes } from '@react-types/shared';
import { DateInputReturnType, SlotsToClasses, DateInputSlots } from '@heroui/theme';
import { DateFieldState } from '@react-stately/datepicker';
import { HTMLHeroUIProps } from '@heroui/system';

type HeroUIBaseProps = Omit<HTMLHeroUIProps<"div">, keyof GroupDOMAttributes | "onChange">;
interface DateInputFieldProps extends HeroUIBaseProps, GroupDOMAttributes {
    /** State for the date field. */
    state: DateFieldState;
    /** Props for the hidden input element for HTML form submission. */
    inputProps: InputHTMLAttributes<HTMLInputElement>;
    /** DateInput classes slots. */
    slots: DateInputReturnType;
    /** DateInput classes. */
    classNames?: SlotsToClasses<DateInputSlots>;
}
declare const DateInputField: react.ForwardRefExoticComponent<DateInputFieldProps & react.RefAttributes<"div">>;

export { DateInputField, type DateInputFieldProps };
