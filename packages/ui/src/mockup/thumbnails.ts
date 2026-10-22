// Hand-drawn SVG thumbnails so the mockup has real-looking previews without image files.

// The width and height give the image an intrinsic size, so it scales like a photo would.
const svg = (body: string, w = 160, h = 120, scale = 8) =>
  `data:image/svg+xml,${encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w * scale}" height="${h * scale}">${body}</svg>`,
  )}`;

const lines = (x: number, y: number, widths: number[], gap: number, h: number, fill: string) =>
  widths.map((w, i) => `<rect x="${x}" y="${y + i * gap}" width="${w}" height="${h}" fill="${fill}"/>`).join("");

export const thumbnails = {
  receipt: svg(
    `<rect width="160" height="120" fill="#e9e4da"/><rect x="48" y="8" width="64" height="112" fill="#fdfcf8"/>` +
      `<rect x="58" y="18" width="30" height="5" fill="#8a8f93"/>` +
      lines(58, 32, [44, 38, 44, 26], 8, 3, "#8a8f93") +
      `<rect x="58" y="70" width="44" height="2" fill="#8a8f93"/><rect x="58" y="78" width="20" height="4" fill="#8a8f93"/><rect x="88" y="78" width="14" height="4" fill="#8a8f93"/>`,
  ),
  mountains: svg(
    `<rect width="160" height="120" fill="#9cc3d5"/><circle cx="118" cy="34" r="12" fill="#f6d27a"/>` +
      `<path d="M0 84 L40 56 L70 76 L104 48 L160 86 V120 H0Z" fill="#4c7a5a"/>` +
      `<path d="M0 98 L60 82 L120 96 L160 90 V120 H0Z" fill="#2f5a43"/><rect y="104" width="160" height="16" fill="#6c8fa3"/>`,
  ),
  lake: svg(
    `<rect width="160" height="120" fill="#f3c9a0"/><circle cx="50" cy="52" r="16" fill="#f7e3b0"/>` +
      `<path d="M0 70 L30 58 L56 66 L90 50 L124 62 L160 54 V120 H0Z" fill="#59606b"/>` +
      `<rect y="78" width="160" height="42" fill="#7b8fa6"/><rect x="38" y="84" width="24" height="3" fill="#f7e3b0" opacity=".7"/>`,
  ),
  cabin: svg(
    `<rect width="160" height="120" fill="#cfe3ea"/><rect y="80" width="160" height="40" fill="#6f9a5b"/>` +
      `<rect x="56" y="56" width="48" height="32" fill="#b23b2e"/><path d="M50 58 L80 36 L110 58Z" fill="#3b3b3b"/>` +
      `<rect x="66" y="66" width="10" height="10" fill="#fff"/><rect x="84" y="70" width="10" height="18" fill="#fff"/>`,
  ),
  document: svg(
    `<rect width="160" height="120" fill="#e6e9eb"/><rect x="36" y="10" width="88" height="110" fill="#ffffff"/>` +
      `<rect x="46" y="20" width="40" height="6" fill="#1a62c6"/>` +
      lines(46, 34, [68, 62, 66, 40], 7, 3, "#9aa3a8") +
      lines(46, 66, [68, 58, 64], 7, 3, "#9aa3a8"),
  ),
  sheet: svg(
    `<rect width="160" height="120" fill="#e3ece6"/><rect x="20" y="14" width="120" height="96" fill="#ffffff"/>` +
      `<rect x="20" y="14" width="120" height="12" fill="#1d6b3c" opacity=".85"/>` +
      Array.from({ length: 6 }, (_, i) => `<rect x="20" y="${38 + i * 12}" width="120" height="1" fill="#c9d3cc"/>`).join("") +
      Array.from({ length: 3 }, (_, i) => `<rect x="${56 + i * 28}" y="26" width="1" height="84" fill="#c9d3cc"/>`).join(""),
  ),
  slides: svg(
    `<rect width="160" height="120" fill="#f1e2d4"/><rect x="16" y="22" width="128" height="76" rx="2" fill="#ffffff"/>` +
      `<rect x="28" y="34" width="60" height="8" fill="#c4581d"/>` +
      lines(28, 50, [48, 40, 52], 8, 3, "#a49a90") +
      `<circle cx="112" cy="66" r="16" fill="#f2b27c"/>`,
  ),
  video: svg(
    `<rect width="160" height="120" fill="#2b3a44"/><circle cx="50" cy="70" r="30" fill="#e07a5f"/>` +
      `<rect x="80" y="30" width="60" height="70" rx="6" fill="#f2cc8f"/>` +
      `<circle cx="80" cy="60" r="16" fill="#ffffff" fill-opacity=".85"/><path d="M75 52 L89 60 L75 68Z" fill="#2b3a44"/>`,
  ),
  floorPlan: svg(
    `<rect width="160" height="120" fill="#f4f6f7"/><rect x="24" y="16" width="112" height="88" fill="none" stroke="#33414a" stroke-width="3"/>` +
      `<path d="M24 60 H84 V104 M84 16 V44 M84 60 H136" fill="none" stroke="#33414a" stroke-width="2"/>` +
      `<path d="M96 104 a12 12 0 0 1 12 -12" fill="none" stroke="#7c8a93"/>`,
  ),
};

/** A large page for the viewer: a receipt or a contract page. */
export const pages = {
  receipt: svg(
    `<rect width="300" height="420" fill="#fdfcf8"/><rect x="40" y="36" width="120" height="16" fill="#2b2f33"/>` +
      lines(40, 70, [180, 150, 200], 14, 6, "#9aa0a4") +
      `<rect x="40" y="128" width="220" height="1" fill="#c9cdd0"/>` +
      Array.from({ length: 8 }, (_, i) => `<rect x="40" y="${146 + i * 22}" width="${120 + ((i * 37) % 60)}" height="7" fill="#6c7378"/><rect x="${230 - ((i * 13) % 20)}" y="${146 + i * 22}" width="${30 + ((i * 13) % 20)}" height="7" fill="#6c7378"/>`).join("") +
      `<rect x="40" y="330" width="220" height="2" fill="#2b2f33"/><rect x="40" y="346" width="70" height="12" fill="#2b2f33"/><rect x="200" y="346" width="60" height="12" fill="#2b2f33"/>`,
    300,
    420,
    2,
  ),
  contract: svg(
    `<rect width="300" height="420" fill="#ffffff"/><rect x="40" y="40" width="140" height="14" fill="#1a62c6"/>` +
      Array.from({ length: 16 }, (_, i) => `<rect x="40" y="${76 + i * 18}" width="${220 - ((i * 29) % 70)}" height="6" fill="#9aa3a8"/>`).join("") +
      `<path d="M60 380 q20 -24 40 0 t40 0" fill="none" stroke="#1a2f4a" stroke-width="2"/>`,
    300,
    420,
    2,
  ),
};
