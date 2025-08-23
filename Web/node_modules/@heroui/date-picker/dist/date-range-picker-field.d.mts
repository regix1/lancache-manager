import { DateInputReturnType, SlotsToClasses, DateInputSlots } from '@heroui/theme';
import { DateValue, AriaDatePickerProps } from '@react-types/datepicker';
import { HTMLHeroUIProps } from '@heroui/system';
import { DateInputProps } from '@heroui/date-input';
import { ReactElement } from 'react';

type HeroUIBaseProps<T extends DateValue> = Omit<HTMLHeroUIProps<"div">, keyof AriaDatePickerProps<T> | "onChange">;
interface Props<T extends DateValue> extends HeroUIBaseProps<T>, AriaDatePickerProps<T>, Pick<DateInputProps, "createCalendar"> {
    /** DateInput classes slots. */
    slots: DateInputReturnType;
    /** DateInput classes. */
    classNames?: SlotsToClasses<DateInputSlots>;
}
type DateRangePickerFieldProps<T extends DateValue = DateValue> = Props<T>;
declare const DateRangePickerField: <T extends DateValue>(props: DateRangePickerFieldProps<T>) => ReactElement;

export { type DateRangePickerFieldProps, type Props, DateRangePickerField as default };
