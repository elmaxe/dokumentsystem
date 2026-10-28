const units = ["B", "KiB", "MiB", "GiB", "TiB"] as const;

/**
 * Formats a byte count for display in binary units, everywhere in the product (§16.5): 1 KiB = 1024 B
 * ("1,4 MiB" in Swedish, "1.4 MiB" in English).
 * Uses one decimal below 10 and none above, so sizes line up in lists.
 */
export function formatBytes(bytes: number, locale: string): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    throw new RangeError(`Invalid byte count: ${bytes}`);
  }

  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  const digits = unit === 0 || value >= 10 ? 0 : 1;
  const number = new Intl.NumberFormat(locale, {
    maximumFractionDigits: digits,
    minimumFractionDigits: digits,
  }).format(value);
  return `${number}\u00a0${units[unit]}`;
}

/** A video or audio length as players show it: "0:07", "3:42", "1:02:05". Rounded down to the second. */
export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) {
    throw new RangeError(`Invalid duration: ${seconds}`);
  }

  const total = Math.floor(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${s}` : `${m}:${s}`;
}
