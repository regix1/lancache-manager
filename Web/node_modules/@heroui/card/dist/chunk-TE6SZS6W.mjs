"use client";
import {
  useCardContext
} from "./chunk-XHGGCEVJ.mjs";

// src/card-footer.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var CardFooter = forwardRef((props, ref) => {
  var _a;
  const { as, className, children, ...otherProps } = props;
  const Component = as || "div";
  const domRef = useDOMRef(ref);
  const { slots, classNames } = useCardContext();
  const footerStyles = clsx(classNames == null ? void 0 : classNames.footer, className);
  return /* @__PURE__ */ jsx(Component, { ref: domRef, className: (_a = slots.footer) == null ? void 0 : _a.call(slots, { class: footerStyles }), ...otherProps, children });
});
CardFooter.displayName = "HeroUI.CardFooter";
var card_footer_default = CardFooter;

export {
  card_footer_default
};
