import { DateInputReturnType, SlotsToClasses, DateInputSlots } from '@heroui/theme';
import { HTMLHeroUIProps } from '@heroui/system';
import { DateFieldState, DateSegment } from '@react-stately/datepicker';

interface DateInputSegmentProps extends HTMLHeroUIProps<"div"> {
    state: DateFieldState;
    segment: DateSegment;
    slots: DateInputReturnType;
    classNames?: SlotsToClasses<DateInputSlots>;
}
declare const DateInputSegment: React.FC<DateInputSegmentProps>;

export { DateInputSegment, type DateInputSegmentProps };
