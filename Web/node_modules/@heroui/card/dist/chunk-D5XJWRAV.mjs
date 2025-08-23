"use client";
import {
  useCardContext
} from "./chunk-XHGGCEVJ.mjs";

// src/card-header.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var CardHeader = forwardRef((props, ref) => {
  var _a;
  const { as, className, children, ...otherProps } = props;
  const Component = as || "div";
  const domRef = useDOMRef(ref);
  const { slots, classNames } = useCardContext();
  const headerStyles = clsx(classNames == null ? void 0 : classNames.header, className);
  return /* @__PURE__ */ jsx(Component, { ref: domRef, className: (_a = slots.header) == null ? void 0 : _a.call(slots, { class: headerStyles }), ...otherProps, children });
});
CardHeader.displayName = "HeroUI.CardHeader";
var card_header_default = CardHeader;

export {
  card_header_default
};
