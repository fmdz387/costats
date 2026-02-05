import fs from "node:fs/promises";
import path from "node:path";
import open from "open";
import ora from "ora";
import { generateInsightsFromHtml } from "./ai.js";
import { renderCard } from "./render.js";
import { validateInsights } from "./schema.js";
import { defaultOutputPath, defaultReportPath } from "./utils.js";

export async function runCli(argv) {
  const [command, ...rest] = argv;
  if (!command || command === "-h" || command === "--help") {
    printHelp();
    return;
  }
  const normalizedCommand = command === "ccinsights" ? "insights" : command;
  if (normalizedCommand !== "insights") {
    throw new Error(`Unknown command: ${command}`);
  }

  const options = parseArgs(rest);
  if (options.help) {
    printHelp();
    return;
  }

  const inputPath = options.input || defaultReportPath();
  const outputPath = path.resolve(options.output || defaultOutputPath());
  await fs.mkdir(path.dirname(outputPath), { recursive: true });

  const spinner = ora({
    text: "Composing your insights card...",
    spinner: "dots",
    isEnabled: Boolean(process.stdout.isTTY)
  }).start();
  try {
    const html = await readReport(inputPath);
    let finalData = await generateInsightsFromHtml({
      model: options.model,
      html
    });

    finalData = applyDerivedFields(finalData);
    finalData = validateInsights(finalData);

    if (options.json) {
      const jsonPath = path.resolve(options.json);
      await fs.writeFile(jsonPath, JSON.stringify(finalData, null, 2), "utf8");
    }

    await renderCard(finalData, outputPath);

    let openError = null;
    if (options.open) {
      try {
        await open(outputPath, { wait: false });
      } catch (err) {
        openError = err;
      }
    }

    spinner.succeed(`Card generated: ${outputPath}`);
    if (openError) {
      const message = openError instanceof Error ? openError.message : String(openError);
      console.warn("costats: unable to open the image: " + message);
    }
  } catch (err) {
    spinner.fail(toUserMessage(err, inputPath));
    process.exitCode = 1;
  }
}

function parseArgs(args) {
  const options = {
    open: true
  };
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (arg === "--input") {
      options.input = args[++i];
    } else if (arg.startsWith("--input=")) {
      options.input = arg.split("=")[1];
    } else if (arg === "--output") {
      options.output = args[++i];
    } else if (arg.startsWith("--output=")) {
      options.output = arg.split("=")[1];
    } else if (arg === "--json") {
      options.json = args[++i];
    } else if (arg.startsWith("--json=")) {
      options.json = arg.split("=")[1];
    } else if (arg === "--model") {
      options.model = args[++i];
    } else if (arg.startsWith("--model=")) {
      options.model = arg.split("=")[1];
    } else if (arg === "--no-open") {
      options.open = false;
    } else if (arg === "--open") {
      options.open = true;
    } else if (arg === "-h" || arg === "--help") {
      options.help = true;
    }
  }
  return options;
}

function applyDerivedFields(data) {
  const outcomes = data.outcomes || [];
  if ((!data.achievementRate || data.achievementRate === 0) && outcomes.length > 0) {
    const total = outcomes.reduce((sum, item) => sum + item.count, 0);
    if (total > 0) {
      const achieved = outcomes
        .filter((item) => /fully|mostly/i.test(item.name))
        .reduce((sum, item) => sum + item.count, 0);
      data.achievementRate = achieved / total;
    }
  }
  if (data.achievementRate && data.achievementRate > 1) {
    data.achievementRate = data.achievementRate / 100;
  }
  return data;
}

async function readReport(inputPath) {
  try {
    await fs.stat(inputPath);
  } catch (err) {
    if (err && typeof err === "object" && err.code === "ENOENT") {
      throw new Error(
        `No Claude Code insights report found at ${inputPath}. Run /insights in Claude Code first.`
      );
    }
    throw err;
  }
  const html = await fs.readFile(inputPath, "utf8");
  if (!html.trim()) {
    throw new Error(
      `Claude Code insights report at ${inputPath} is empty. Run /insights in Claude Code again.`
    );
  }
  return html;
}

function toUserMessage(err, inputPath) {
  const message = err instanceof Error ? err.message : String(err);
  if (message.includes("Claude OAuth credentials not found")) {
    return "Claude Code is not signed in. Open Claude Code and sign in, then retry.";
  }
  if (message.includes("Claude OAuth token expired")) {
    return "Claude Code sign-in expired. Re-authenticate in Claude Code, then retry.";
  }
  if (message.includes("ENOENT") && message.includes(inputPath)) {
    return `No Claude Code insights report found at ${inputPath}. Run /insights in Claude Code first.`;
  }
  return message;
}

function printHelp() {
  const defaultInput = defaultReportPath();
  const defaultOutput = defaultOutputPath();
  console.log(
    `Costats Insights Card CLI\n\nDefault run:\n  npx costats ccinsights\n\nUsage:\n  costats insights [options]\n  costats ccinsights [options]\n\nOptions:\n  --input <path>   Path to report.html (default: ${defaultInput})\n  --output <path>  Output PNG path (default: ${defaultOutput})\n  --json <path>    Write extracted JSON to this file\n  --model <name>   Claude model override\n  --no-open        Do not open the generated image\n  -h, --help       Show help\n\nRequires Claude OAuth credentials in ~/.claude/.credentials.json`
  );
}
