import { PDFDocument } from "pdf-lib";
import { describe, expect, it } from "vitest";
import { pdfName, scanName, scanToPdf } from "./scanPdf";

// A 20 × 40 JPEG.
const jpeg = Uint8Array.from(Buffer.from("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABsSFBcUERsXFhceHBsgKEIrKCUlKFE6PTBCYFVlZF9VXVtqeJmBanGQc1tdhbWGkJ6jq62rZ4C8ybqmx5moq6T/2wBDARweHigjKE4rK06kbl1upKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKSkpKT/wAARCAAoABQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDNooormPbCiiigAooooAKKKKACiiigAooooA//2Q==", "base64"));

describe("scanToPdf", () => {
  it("makes a page per photo, A4 wide and in the photo's proportions", async () => {
    const pdf = await PDFDocument.load(await scanToPdf([jpeg, jpeg], "Kvitto"));
    expect(pdf.getPageCount()).toBe(2);
    const { width, height } = pdf.getPage(0).getSize();
    expect(width).toBeCloseTo(595.28);
    expect(height).toBeCloseTo(595.28 * 2);
    expect(pdf.getTitle()).toBe("Kvitto");
  });

  it("refuses a scan without pages", async () => {
    await expect(scanToPdf([], "Empty")).rejects.toThrow();
  });
});

describe("names", () => {
  it("names a scan by its local date and time", () => {
    expect(scanName("Skanning", new Date(2026, 10, 2, 9, 5))).toBe("Skanning 2026-11-02 09.05");
  });

  it.each([
    ["Kvitto", "Kvitto.pdf"],
    [" Kvitto.PDF ", "Kvitto.PDF"],
    ["a.pdf.txt", "a.pdf.txt.pdf"],
  ])("adds .pdf to %j", (name, expected) => {
    expect(pdfName(name)).toBe(expected);
  });
});
