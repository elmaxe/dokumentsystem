import { useRef, useState, type KeyboardEvent, type MouseEvent, type PointerEvent } from "react";
import type { FileItem } from "./files";

export type SelectionOptions<T extends FileItem> = {
  /** The items in display order. */
  items: T[];
  selected: string[];
  onSelectedChange: (ids: string[]) => void;
  onOpen?: (item: T) => void;
  onTrash?: (ids: string[]) => void;
};

const longPressMs = 500;

/**
 * Selection and keyboard behaviour shared by FileGrid and FileList (docs/design.md §16.3, §16.5).
 *
 * Mouse: click selects, Ctrl/Cmd-click toggles, Shift-click selects a range, double-click opens.
 * Touch: tap opens, long-press selects, and taps toggle while anything is selected.
 * Keyboard: arrows move (spatially, so it works across grid rows), Shift+arrow extends, Space toggles,
 * Enter opens, Delete trashes, Ctrl/Cmd+A selects all and Escape clears.
 */
export function useFileSelection<T extends FileItem>({
  items,
  selected,
  onSelectedChange,
  onOpen,
  onTrash,
}: SelectionOptions<T>) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const anchor = useRef<string | null>(null);
  const press = useRef<{ timer: number; x: number; y: number } | null>(null);
  const suppressClick = useRef(false);

  const ids = items.map((item) => item.id);
  const current = focusedId && ids.includes(focusedId) ? focusedId : (selected[0] ?? ids[0]);

  const toggle = (id: string) =>
    onSelectedChange(selected.includes(id) ? selected.filter((s) => s !== id) : [...selected, id]);

  const range = (from: string, to: string) => {
    const [a, b] = [ids.indexOf(from), ids.indexOf(to)].sort((x, y) => x - y);
    return ids.slice(a ?? 0, (b ?? a ?? 0) + 1);
  };

  const element = (id: string) =>
    containerRef.current?.querySelector<HTMLElement>(`[data-item-id="${CSS.escape(id)}"]`);

  const moveFocus = (id: string | undefined, extend: boolean) => {
    if (!id) return;
    setFocusedId(id);
    element(id)?.focus();
    if (extend) onSelectedChange(range(anchor.current ?? current ?? id, id));
    else anchor.current = id;
  };

  /** The nearest item in the row above or below, by horizontal centre. */
  const vertical = (from: string, direction: 1 | -1) => {
    const origin = element(from)?.getBoundingClientRect();
    if (!origin) return undefined;
    const centre = origin.left + origin.width / 2;
    let best: { id: string; dy: number; dx: number } | undefined;
    for (const id of ids) {
      const rect = element(id)?.getBoundingClientRect();
      if (!rect) continue;
      const dy = (rect.top - origin.top) * direction;
      if (dy <= 1) continue;
      const dx = Math.abs(rect.left + rect.width / 2 - centre);
      if (!best || dy < best.dy - 1 || (Math.abs(dy - best.dy) <= 1 && dx < best.dx)) {
        best = { id, dy, dx };
      }
    }
    return best?.id;
  };

  const cancelPress = () => {
    if (press.current) window.clearTimeout(press.current.timer);
    press.current = null;
  };

  const itemProps = (item: T) => ({
    "data-item-id": item.id,
    tabIndex: item.id === current ? 0 : -1,
    "aria-selected": selected.includes(item.id),
    onFocus: () => setFocusedId(item.id),
    onClick: (event: MouseEvent) => {
      if (suppressClick.current) {
        suppressClick.current = false;
        return;
      }
      const touch = (event.nativeEvent as globalThis.PointerEvent).pointerType === "touch";
      if (touch) {
        if (selected.length > 0) toggle(item.id);
        else onOpen?.(item);
      } else if (event.shiftKey && anchor.current) {
        onSelectedChange(range(anchor.current, item.id));
      } else if (event.metaKey || event.ctrlKey) {
        toggle(item.id);
        anchor.current = item.id;
      } else {
        onSelectedChange([item.id]);
        anchor.current = item.id;
      }
    },
    onDoubleClick: () => onOpen?.(item),
    onPointerDown: (event: PointerEvent) => {
      if (event.pointerType !== "touch") return;
      cancelPress();
      press.current = {
        x: event.clientX,
        y: event.clientY,
        timer: window.setTimeout(() => {
          press.current = null;
          suppressClick.current = true;
          toggle(item.id);
          anchor.current = item.id;
          navigator.vibrate?.(10);
        }, longPressMs),
      };
    },
    onPointerMove: (event: PointerEvent) => {
      const start = press.current;
      if (start && Math.hypot(event.clientX - start.x, event.clientY - start.y) > 10) cancelPress();
    },
    onPointerUp: cancelPress,
    onPointerCancel: cancelPress,
    onContextMenu: (event: MouseEvent) => {
      // Long-press fires contextmenu on touch; it selects instead of opening a menu there.
      if (press.current || suppressClick.current) event.preventDefault();
      // Right-clicking an unselected item selects it, so the menu acts on what it shows.
      else if (!selected.includes(item.id)) onSelectedChange([item.id]);
    },
    onKeyDown: (event: KeyboardEvent) => {
      const index = ids.indexOf(item.id);
      const extend = event.shiftKey;
      const mod = event.metaKey || event.ctrlKey;
      let handled = true;
      switch (event.key) {
        case "ArrowRight":
          moveFocus(ids[index + 1], extend);
          break;
        case "ArrowLeft":
          moveFocus(ids[index - 1], extend);
          break;
        case "ArrowDown":
          moveFocus(vertical(item.id, 1), extend);
          break;
        case "ArrowUp":
          moveFocus(vertical(item.id, -1), extend);
          break;
        case "Home":
          moveFocus(ids[0], extend);
          break;
        case "End":
          moveFocus(ids.at(-1), extend);
          break;
        case " ":
          toggle(item.id);
          anchor.current = item.id;
          break;
        case "Enter":
          onOpen?.(item);
          break;
        case "Delete":
          onTrash?.(selected.includes(item.id) ? selected : [item.id]);
          break;
        case "Escape":
          if (selected.length === 0) handled = false;
          else onSelectedChange([]);
          break;
        case "a":
        case "A":
          if (mod) onSelectedChange(ids);
          else handled = false;
          break;
        default:
          handled = false;
      }
      if (handled) {
        event.preventDefault();
        event.stopPropagation();
      }
    },
  });

  return { containerRef, itemProps };
}
