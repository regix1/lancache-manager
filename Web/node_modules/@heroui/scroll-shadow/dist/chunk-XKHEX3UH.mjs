"use client";

// src/use-scroll-shadow.ts
import { mapPropsVariants } from "@heroui/system";
import { scrollShadow } from "@heroui/theme";
import { useDOMRef } from "@heroui/react-utils";
import { useDataScrollOverflow } from "@heroui/use-data-scroll-overflow";
import { useMemo } from "react";
import { objectToDeps } from "@heroui/shared-utils";
function useScrollShadow(originalProps) {
  var _a;
  const [props, variantProps] = mapPropsVariants(originalProps, scrollShadow.variantKeys);
  const {
    ref,
    as,
    children,
    className,
    style,
    size = 40,
    offset = 0,
    visibility = "auto",
    isEnabled = true,
    onVisibilityChange,
    ...otherProps
  } = props;
  const Component = as || "div";
  const domRef = useDOMRef(ref);
  useDataScrollOverflow({
    domRef,
    offset,
    visibility,
    isEnabled,
    onVisibilityChange,
    updateDeps: [children],
    overflowCheck: (_a = originalProps.orientation) != null ? _a : "vertical"
  });
  const styles = useMemo(
    () => scrollShadow({
      ...variantProps,
      className
    }),
    [objectToDeps(variantProps), className]
  );
  const getBaseProps = (props2 = {}) => {
    var _a2;
    return {
      ref: domRef,
      className: styles,
      "data-orientation": (_a2 = originalProps.orientation) != null ? _a2 : "vertical",
      style: {
        "--scroll-shadow-size": `${size}px`,
        ...style,
        ...props2.style
      },
      ...otherProps,
      ...props2
    };
  };
  return { Component, styles, domRef, children, getBaseProps };
}

export {
  useScrollShadow
};
