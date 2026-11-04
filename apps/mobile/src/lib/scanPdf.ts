import { PDFDocument } from "pdf-lib";

/** A4's width in points: scanned pages get it, and a height in proportion to the photo. */
const pageWidth = 595.28;

/**
 * One PDF of scanned pages, a JPEG per page, in order (docs/design.md §15.3). The JPEGs go in as they are, without
 * decoding them again, and the server reads the text in them as it does any scan.
 */
export async function scanToPdf(pages: Uint8Array[], title: string): Promise<Uint8Array> {
  if (pages.length === 0) throw new Error("A scan needs at least one page");
  const pdf = await PDFDocument.create();
  pdf.setTitle(title);
  pdf.setCreator("Dokumentsystem");
  for (const bytes of pages) {
    const image = await pdf.embedJpg(bytes);
    const height = (pageWidth * image.height) / image.width;
    pdf.addPage([pageWidth, height]).drawImage(image, { x: 0, y: 0, width: pageWidth, height });
  }
  return pdf.save();
}

/** "Scan 2026-11-02 21.13": the name a scan gets until someone changes it. Dots, since some systems refuse colons. */
export function scanName(prefix: string, date: Date): string {
  const two = (n: number) => String(n).padStart(2, "0");
  return `${prefix} ${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())} ${two(date.getHours())}.${two(date.getMinutes())}`;
}

/** Adds ".pdf" unless the name ends with it already. */
export const pdfName = (name: string) => (/\.pdf$/i.test(name.trim()) ? name.trim() : `${name.trim()}.pdf`);
