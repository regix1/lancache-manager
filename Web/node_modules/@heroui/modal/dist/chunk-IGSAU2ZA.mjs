"use client";
import {
  useModalContext
} from "./chunk-UX6VCJJD.mjs";

// src/modal-header.tsx
import { useEffect } from "react";
import { forwardRef } from "@heroui/system";
import { useDOMRef } from "@heroui/react-utils";
import { clsx } from "@heroui/shared-utils";
import { jsx } from "react/jsx-runtime";
var ModalHeader = forwardRef((props, ref) => {
  const { as, children, className, ...otherProps } = props;
  const { slots, classNames, headerId, setHeaderMounted } = useModalContext();
  const domRef = useDOMRef(ref);
  const Component = as || "header";
  useEffect(() => {
    setHeaderMounted(true);
    return () => setHeaderMounted(false);
  }, [setHeaderMounted]);
  return /* @__PURE__ */ jsx(
    Component,
    {
      ref: domRef,
      className: slots.header({ class: clsx(classNames == null ? void 0 : classNames.header, className) }),
      id: headerId,
      ...otherProps,
      children
    }
  );
});
ModalHeader.displayName = "HeroUI.ModalHeader";
var modal_header_default = ModalHeader;

export {
  modal_header_default
};
