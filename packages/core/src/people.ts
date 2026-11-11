/**
 * The letters an avatar shows when the person has no picture (docs/design.md §5.1): the first letter of the first and
 * of the last word of their name, so "Axel Elmarsson" is "AE" and "Anna Maria Svensson" is "AS". A one-word name,
 * or an email used as the name, gives one letter. Letters are uppercased in the given language.
 */
export function initials(name: string, locale?: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return "";
  const first = firstLetter(words[0]!);
  const last = words.length > 1 ? firstLetter(words[words.length - 1]!) : "";
  return (first + last).toLocaleUpperCase(locale);
}

/** The first character as the reader sees it, so letters outside the BMP or with combining marks stay whole. */
function firstLetter(word: string): string {
  const letter = /[\p{L}\p{N}][\p{M}]*/u.exec(word);
  return letter ? letter[0] : Array.from(word)[0]!;
}
