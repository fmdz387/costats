import path from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const root = path.resolve(__dirname, "..");

await build({
  entryPoints: [path.join(root, "src", "cli-entry.js")],
  outfile: path.join(root, "dist", "costats.js"),
  bundle: true,
  platform: "node",
  format: "esm",
  target: ["node18"],
  external: ["playwright"],
  banner: {
    js: "#!/usr/bin/env node"
  },
  sourcemap: true,
  legalComments: "eof"
});
