/// <reference types="nativewind/types" />

// Metro bundles the stylesheet that NativeWind compiles; it has no exports.
declare module "*.css";

// pdf.js's worker, run on the web view's own thread (src/viewers/PdfDom.tsx).
declare module "pdfjs-dist/build/pdf.worker.mjs";
