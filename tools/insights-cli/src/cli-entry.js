import { runCli } from "./index.js";

runCli(process.argv.slice(2)).catch((err) => {
  const message = err instanceof Error ? err.message : String(err);
  console.error("costats: " + message);
  process.exitCode = 1;
});
