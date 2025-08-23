"use client";
import {
  useCardContext
} from "./chunk-XHGGCEVJ.mjs";

// src/card-body.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var CardBody = forwardRef((props, ref) => {
  var _a;
  const { as, className, children, ...otherProps } = props;
  const Component = as || "div";
  const domRef = useDOMRef(ref);
  const { slots, classNames } = useCardContext();
  const bodyStyles = clsx(classNames == null ? void 0 : classNames.body, className);
  return /* @__PURE__ */ jsx(Component, { ref: domRef, className: (_a = slots.body) == null ? void 0 : _a.call(slots, { class: bodyStyles }), ...otherProps, children });
});
CardBody.displayName = "HeroUI.CardBody";
var card_body_default = CardBody;

export {
  card_body_default
};
