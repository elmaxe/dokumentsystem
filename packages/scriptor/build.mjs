// Builds Scriptor from the commit pinned in package.json into dist/ (decision 42, docs/design.md §14):
//   dist/wasm/  the engine, compiled to WebAssembly by wasm-pack
//   dist/core/  its headless canvas view (@truespar/scriptor-core), TypeScript that Vite compiles
// Needs git, a Rust toolchain with the wasm32-unknown-unknown target, and wasm-pack, or else Docker, which runs the same
// build in the image's `scriptor` stage. Without either it writes a stub,
// so the web app still builds and Word files open as PDFs; SCRIPTOR_REQUIRED=1 (CI, Docker) makes that an error.
import { execFileSync } from "node:child_process";
import {
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const { repository, commit } = JSON.parse(
  readFileSync(join(here, "package.json"), "utf8"),
).scriptor;
const dist = join(here, "dist");
const stamp = join(dist, "COMMIT");
const required = process.env.SCRIPTOR_REQUIRED === "1";

if (existsSync(stamp) && readFileSync(stamp, "utf8").trim() === commit) {
  console.log(`Scriptor ${commit.slice(0, 7)} is already built.`);
  process.exit(0);
}

const run = (cmd, args, cwd) =>
  execFileSync(cmd, args, { cwd, stdio: "inherit", shell: process.platform === "win32" });
const has = (cmd) => {
  try {
    execFileSync(cmd, ["--version"], { stdio: "ignore", shell: process.platform === "win32" });
    return true;
  } catch {
    return false;
  }
};

// Without Rust here, Docker can build it: the image's `scriptor` stage does exactly this build, and Docker caches it, so
// only the first run is slow. That covers `aspire run`, which already needs Docker.
if ((!has("wasm-pack") || !has("cargo")) && has("docker") && process.env.SCRIPTOR_DOCKER !== "0") {
  const repo = resolve(here, "..", "..");
  const image = `dokumentsystem-scriptor:${commit.slice(0, 12)}`;
  const container = `dokumentsystem-scriptor-${process.pid}`;
  console.log(
    `Building Scriptor ${commit.slice(0, 7)} in Docker (the image's scriptor stage). The first time takes a few minutes.`,
  );
  try {
    run(
      "docker",
      ["build", "--target", "scriptor", "--tag", image, "--file", join(repo, "Dockerfile"), repo],
      repo,
    );
    rmSync(dist, { recursive: true, force: true });
    run("docker", ["create", "--name", container, image], repo);
    try {
      run("docker", ["cp", `${container}:/repo/packages/scriptor/dist`, dist], repo);
    } finally {
      run("docker", ["rm", container], repo);
    }
    console.log(`Built Scriptor ${commit.slice(0, 7)} in Docker.`);
    process.exit(0);
  } catch (error) {
    if (required) throw error;
    console.warn(`Couldn't build Scriptor in Docker (is Docker running?): ${error.message}`);
  }
}

if (!has("wasm-pack") || !has("cargo")) {
  const message =
    "Scriptor needs Rust and wasm-pack (cargo install wasm-pack), or Docker. Word files will open as PDFs.";
  if (required) {
    console.error(message);
    process.exit(1);
  }
  console.warn(`${message} Writing a stub.`);
  rmSync(dist, { recursive: true, force: true });
  mkdirSync(join(dist, "core"), { recursive: true });
  writeFileSync(
    join(dist, "core", "index.ts"),
    `export const available = false;
export class ScriptorView {
  static create(): Promise<never> {
    return Promise.reject(new Error("Scriptor wasn't built: it needs Docker, or Rust and wasm-pack."));
  }
}
`,
  );
  writeFileSync(stamp, "stub\n");
  process.exit(0);
}

// A shallow fetch of just the pinned commit, kept in .cache so a rebuild doesn't download it again.
const source = join(here, ".cache", "scriptor");
if (!existsSync(join(source, ".git"))) {
  mkdirSync(source, { recursive: true });
  run("git", ["init", "--quiet"], source);
  run("git", ["remote", "add", "origin", repository], source);
}
run("git", ["fetch", "--quiet", "--depth", "1", "origin", commit], source);
run("git", ["checkout", "--quiet", "--force", "--detach", commit], source);

rmSync(dist, { recursive: true, force: true });
run(
  "wasm-pack",
  [
    "build",
    "crates/scriptor-wasm",
    "--target",
    "web",
    "--release",
    "--no-pack",
    "--out-dir",
    resolve(dist, "wasm"),
  ],
  source,
);
rmSync(join(dist, "wasm", ".gitignore"), { force: true });

// The view's sources, minus tests, importing the engine by path instead of by its unpublished package name.
const core = join(dist, "core");
cpSync(join(source, "packages", "core", "src"), core, {
  recursive: true,
  filter: (f) => !/\.test\.ts$/.test(f),
});
for (const file of readdirSync(core)) {
  const path = join(core, file);
  writeFileSync(
    path,
    readFileSync(path, "utf8").replaceAll(
      "'@truespar/scriptor-wasm'",
      "'../wasm/scriptor_wasm.js'",
    ),
  );
}
writeFileSync(
  join(core, "index.ts"),
  `${readFileSync(join(core, "index.ts"), "utf8")}\nexport const available = true;\n`,
);

for (const notice of ["LICENSE-MIT", "LICENSE-APACHE", "THIRD-PARTY-NOTICES.md"]) {
  cpSync(join(source, notice), join(dist, notice));
}
cpSync(
  join(source, "crates", "scriptor-fonts", "fonts", "NOTICES.md"),
  join(dist, "FONT-NOTICES.md"),
);
writeFileSync(stamp, `${commit}\n`);
console.log(`Built Scriptor ${commit.slice(0, 7)}.`);
