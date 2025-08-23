"use client";

// src/use-drawer.ts
import { drawer } from "@heroui/theme";
import { useDOMRef } from "@heroui/react-utils";
import { useCallback, useMemo } from "react";
import { TRANSITION_EASINGS } from "@heroui/framer-utils";
import { clsx, isEmpty } from "@heroui/shared-utils";
function useDrawer(originalProps) {
  const {
    ref,
    className,
    classNames,
    placement = "right",
    scrollBehavior = "inside",
    size = "md",
    motionProps: drawerMotionProps,
    ...otherProps
  } = originalProps;
  const domRef = useDOMRef(ref);
  const motionProps = useMemo(() => {
    if (!isEmpty(drawerMotionProps)) return drawerMotionProps;
    const key = placement === "left" || placement === "right" ? "x" : "y";
    return {
      variants: {
        enter: {
          [key]: 0,
          transition: {
            [key]: {
              duration: 0.2,
              ease: TRANSITION_EASINGS.easeOut
            }
          }
        },
        exit: {
          [key]: placement === "top" || placement === "left" ? "-100%" : "100%",
          transition: {
            [key]: {
              duration: 0.1,
              ease: TRANSITION_EASINGS.easeIn
            }
          }
        }
      }
    };
  }, [placement, drawerMotionProps]);
  const baseStyles = clsx(classNames == null ? void 0 : classNames.base, className);
  const slots = useMemo(
    () => drawer({
      size,
      placement
    }),
    [size, placement]
  );
  const getModalProps = useCallback(() => {
    return {
      classNames: {
        ...classNames,
        base: slots.base({ class: baseStyles })
      },
      motionProps,
      scrollBehavior,
      size,
      ...otherProps
    };
  }, [baseStyles, classNames, motionProps, scrollBehavior, size, otherProps]);
  return { domRef, getModalProps };
}

export {
  useDrawer
};
