import { Checkbox as CheckboxPrimitive } from "radix-ui";
import { Icon } from "./Icon";

export type CheckboxProps = {
  checked: boolean | "indeterminate";
  onCheckedChange?: (checked: boolean) => void;
  label: string;
};

export function Checkbox({ checked, onCheckedChange, label }: CheckboxProps) {
  return (
    <CheckboxPrimitive.Root
      checked={checked}
      onCheckedChange={(value) => onCheckedChange?.(value === true)}
      aria-label={label}
      onClick={(event) => event.stopPropagation()}
      className="relative inline-flex size-4.5 shrink-0 cursor-pointer items-center justify-center rounded-xs border-2 border-outline text-on-primary before:absolute before:-inset-3 data-[state=checked]:border-primary data-[state=checked]:bg-primary data-[state=indeterminate]:border-primary data-[state=indeterminate]:bg-primary"
    >
      <CheckboxPrimitive.Indicator className="inline-flex">
        <Icon name={checked === "indeterminate" ? "remove" : "check"} size={16} />
      </CheckboxPrimitive.Indicator>
    </CheckboxPrimitive.Root>
  );
}
