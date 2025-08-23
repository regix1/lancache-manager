type UseIsMountedProps = {
    rerender?: boolean;
    delay?: number;
};
declare function useIsMounted(props?: UseIsMountedProps): (boolean | (() => boolean))[];
type UseIsMountedReturn = ReturnType<typeof useIsMounted>;

export { type UseIsMountedProps, type UseIsMountedReturn, useIsMounted };
