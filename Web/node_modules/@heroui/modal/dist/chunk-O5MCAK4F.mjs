"use client";
import {
  useModalContext
} from "./chunk-UX6VCJJD.mjs";

// src/modal-footer.tsx
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var ModalFooter = forwardRef((props, ref) => {
  const { as, children, className, ...otherProps } = props;
  const { slots, classNames } = useModalContext();
  const domRef = useDOMRef(ref);
  const Component = as || "footer";
  return /* @__PURE__ */ jsx(
    Component,
    {
      ref: domRef,
      className: slots.footer({ class: clsx(classNames == null ? void 0 : classNames.footer, className) }),
      ...otherProps,
      children
    }
  );
});
ModalFooter.displayName = "HeroUI.ModalFooter";
var modal_footer_default = ModalFooter;

export {
  modal_footer_default
};
