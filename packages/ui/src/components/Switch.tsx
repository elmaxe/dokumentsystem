import { Switch as SwitchPrimitive } from "radix-ui";

export type SwitchProps = {
  checked: boolean;
  onCheckedChange?: (checked: boolean) => void;
  label?: string;
  id?: string;
};

export function Switch({ checked, onCheckedChange, label, id }: SwitchProps) {
  return (
    <SwitchPrimitive.Root
      id={id}
      checked={checked}
      onCheckedChange={onCheckedChange}
      aria-label={label}
      className="group relative inline-flex h-8 w-13 shrink-0 cursor-pointer items-center rounded-full border-2 border-outline bg-surface-container-highest transition-colors duration-(--duration-medium) data-[state=checked]:border-primary data-[state=checked]:bg-primary"
    >
      <SwitchPrimitive.Thumb className="block size-4 translate-x-1.5 rounded-full bg-outline transition-all duration-(--duration-medium) ease-emphasized data-[state=checked]:size-6 data-[state=checked]:translate-x-5.5 data-[state=checked]:bg-on-primary" />
    </SwitchPrimitive.Root>
  );
}
