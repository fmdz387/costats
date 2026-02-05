import fs from "node:fs/promises";
import fsSync from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn, spawnSync } from "node:child_process";
import { escapeHtml, formatDateRange, getPlaywrightCacheDir } from "./utils.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, "..");
let playwrightLoadPromise = null;
let attemptedPlaywrightInstall = false;

// Funny titles based on user stats - classic programmer humor
const TITLES = {
  // Hours-based (primary)
  hours: [
    { min: 2000, title: "localhost Is My Home", reason: "2000+ hours. 127.0.0.1 is the only address you know." },
    { min: 1000, title: "Works On My Machine", reason: "1000+ hours. You should get that certified." },
    { min: 500, title: "Senior Stack Overflow Dev", reason: "500+ hours of copying code with extra steps." },
    { min: 200, title: "console.log Specialist", reason: "200+ hours. Debuggers are for quitters." },
    { min: 100, title: "It's Not a Bug", reason: "100+ hours. It's an undocumented feature." },
    { min: 50, title: "TODO: Fix Later", reason: "50+ hours. Later never comes." },
    { min: 20, title: "git push --force Enjoyer", reason: "20+ hours. History is overrated anyway." },
    { min: 0, title: "Hello World Graduate", reason: "Just started. The bugs are waiting for you." }
  ],
  // Messages per day
  msgsPerDay: [
    { min: 1000, title: "Rubber Duck Replacement", reason: "1000+ msgs/day. Claude is your duck now." },
    { min: 500, title: "Have You Tried Asking?", reason: "500+ msgs/day. Turning it off didn't work." },
    { min: 300, title: "Pair Programmer Solo", reason: "300+ msgs/day. Claude is your only coworker." },
    { min: 200, title: "LGTM Speedrunner", reason: "200+ msgs/day. Looks good to me. Ship it." },
    { min: 100, title: "Code Review: Myself", reason: "100+ msgs/day. Self-approved PRs only." }
  ],
  // Achievement rate
  achievement: [
    { max: 0.3, title: "git reset --hard Expert", reason: "Under 30% achieved. Nuclear option enthusiast." },
    { max: 0.5, title: "Ctrl+Z Speedrunner", reason: "Under 50% achieved. Undo is your main feature." },
    { max: 0.7, title: "rm -rf Survivor", reason: "You break things professionally. Sometimes on purpose." }
  ],
  // Lines removed > added
  destroyer: [
    { ratio: 2, title: "Delete Key Advocate", reason: "2x more deleted. The best code is no code." },
    { ratio: 1, title: "Legacy Code Assassin", reason: "Deleted more than wrote. Doing God's work." }
  ],
  // High sessions
  sessions: [
    { min: 5000, title: "node_modules Collector", reason: "5000+ sessions. Dependencies have dependencies." },
    { min: 2000, title: "Merge Conflict Veteran", reason: "2000+ sessions. You've seen some things." },
    { min: 1000, title: "99 Bugs In The Code", reason: "1000+ sessions. Take one down, 127 more appear." }
  ],
  // Night owl
  nightOwl: [
    { title: "2AM Deployment Hero", reason: "Peak coding at night. What could go wrong?" }
  ],
  // Morning person
  earlyBird: [
    { title: "Standup Meeting Survivor", reason: "Morning coder. Your Jira is always updated." }
  ]
};

// Coding roasts based on user stats
const ROASTS = {
  // Hours-based roasts
  hours: [
    { min: 1000, roasts: [
      "Your GitHub contributions look like a cry for help.",
      "Grass has mass-reported your account for neglect.",
      "Your chair has a more active social life than you.",
      "Even your IDE is begging you to go outside.",
      "Your vitamin D levels are now a rounding error."
    ]},
    { min: 500, roasts: [
      "500+ hours? Your IDE has Stockholm syndrome.",
      "Your mouse has requested a wellness check.",
      "The sun filed a missing persons report on you.",
      "Your posture is now a cautionary tale.",
      "Local therapists are fighting over your case."
    ]},
    { min: 200, roasts: [
      "200 hours and still Googling 'how to center a div'?",
      "Your rubber duck quit and filed for unemployment.",
      "Stack Overflow mods recognize you by IP address.",
      "Your commits are 90% 'fixed typo' messages.",
      "Even ChatGPT has started ghosting you."
    ]},
    { min: 50, roasts: [
      "Your code works but nobody knows why. Including you.",
      "50 hours in and your imposter syndrome has imposter syndrome.",
      "You copy-paste with the confidence of a senior dev.",
      "Your debugging strategy is mass console.log.",
      "Tutorial hell called. They miss you."
    ]}
  ],
  // Night owl roasts
  nightOwl: [
    "3AM commits? Your sleep schedule is a war crime.",
    "The bags under your eyes have their own zip code.",
    "Your circadian rhythm filed for bankruptcy.",
    "Vampires think you need to go outside more.",
    "Your melatonin gave up and moved out."
  ],
  // Morning roasts
  morning: [
    "6AM commits? Seek professional help immediately.",
    "Morning coding is a felony in 12 countries.",
    "Your productivity is a personal attack on night owls.",
    "Nobody asked for this energy before coffee."
  ],
  // High messages per day
  chatty: [
    "You talk to Claude more than your own family.",
    "Your message history reads like a desperate monologue.",
    "Claude's context window is just you screaming.",
    "Do you breathe between prompts or is that optional?"
  ],
  // Code destroyer (more removed than added)
  destroyer: [
    "You delete code like it owes you money.",
    "Your git log is basically a murder mystery.",
    "The best code you wrote was rm -rf.",
    "You're not refactoring. You're running a demolition service."
  ],
  // Low achievement rate
  undoer: [
    "Ctrl+Z is carrying your entire career.",
    "Your undo history is longer than your resume.",
    "You speedrun changing your mind professionally.",
    "Git revert has a restraining order against you."
  ],
  // High sessions
  sessions: [
    "Your session count screams commitment issues.",
    "Each new session is you rage-quitting in disguise.",
    "You start projects like you start diets.",
    "Your terminal history is just 'exit' on repeat."
  ],
  // Default roasts
  default: [
    "Your code compiles. That's the nicest thing anyone can say.",
    "Somewhere a CS professor is crying because of you.",
    "Your code is modern art. Nobody gets it.",
    "At least your bugs are consistent."
  ]
};

function generateRoast(data) {
  const hours = safeNumber(data.totals?.hours);
  const msgsPerDay = safeNumber(data.messagesPerDay);
  const sessions = safeNumber(data.totals?.sessions);
  const achievement = safeNumber(data.achievementRate, 1);
  const linesAdded = safeNumber(data.lines?.added);
  const linesRemoved = safeNumber(data.lines?.removed);
  const peakPeriod = (data.peakPeriod || "").toLowerCase();

  // Build pool of applicable roasts
  const pool = [];

  // Hours-based roasts
  for (const tier of ROASTS.hours) {
    if (hours >= tier.min) {
      pool.push(...tier.roasts);
      break;
    }
  }

  // Night owl
  if (peakPeriod.includes("night") || peakPeriod.includes("evening")) {
    pool.push(...ROASTS.nightOwl);
  }

  // Morning person
  if (peakPeriod.includes("morning")) {
    pool.push(...ROASTS.morning);
  }

  // Chatty
  if (msgsPerDay >= 200) {
    pool.push(...ROASTS.chatty);
  }

  // Code destroyer
  if (linesRemoved > linesAdded && linesAdded > 0) {
    pool.push(...ROASTS.destroyer);
  }

  // Undoer
  if (achievement < 0.7 && achievement > 0) {
    pool.push(...ROASTS.undoer);
  }

  // High sessions
  if (sessions >= 1000) {
    pool.push(...ROASTS.sessions);
  }

  // If no specific roasts matched, use defaults
  if (pool.length === 0) {
    pool.push(...ROASTS.default);
  }

  // Pick a deterministic roast based on data hash
  const hash = (hours + sessions + msgsPerDay) % pool.length;
  return pool[Math.floor(hash)] || pool[0];
}

function getFunnyTitle(data) {
  const hours = safeNumber(data.totals?.hours);
  const msgsPerDay = safeNumber(data.messagesPerDay);
  const achievement = safeNumber(data.achievementRate, 1);
  const sessions = safeNumber(data.totals?.sessions);
  const linesAdded = safeNumber(data.lines?.added);
  const linesRemoved = safeNumber(data.lines?.removed);
  const peakPeriod = data.peakPeriod || "";

  // Check special conditions first
  if (linesRemoved > 0 && linesAdded > 0) {
    const ratio = linesRemoved / linesAdded;
    if (ratio >= 2) return TITLES.destroyer[0];
    if (ratio > 1) return TITLES.destroyer[1];
  }

  // Check achievement rate
  if (achievement > 0 && achievement < 0.3) return TITLES.achievement[0];
  if (achievement >= 0.3 && achievement < 0.5) return TITLES.achievement[1];
  if (achievement >= 0.5 && achievement < 0.7) return TITLES.achievement[2];

  // Check peak time
  if (peakPeriod.toLowerCase().includes("night") || peakPeriod.toLowerCase().includes("evening")) {
    if (hours >= 100) return TITLES.nightOwl[0];
  }
  if (peakPeriod.toLowerCase().includes("morning")) {
    if (hours >= 100) return TITLES.earlyBird[0];
  }

  // Check msgs per day
  for (const t of TITLES.msgsPerDay) {
    if (msgsPerDay >= t.min) return t;
  }

  // Check sessions
  for (const t of TITLES.sessions) {
    if (sessions >= t.min) return t;
  }

  // Default to hours-based
  for (const t of TITLES.hours) {
    if (hours >= t.min) return t;
  }

  return TITLES.hours[TITLES.hours.length - 1];
}

// Safe number extraction with fallback
function safeNumber(value, fallback = 0) {
  if (value === null || value === undefined) return fallback;
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value === "string") {
    const parsed = parseFloat(value.replace(/[^0-9.-]/g, ""));
    return Number.isFinite(parsed) ? parsed : fallback;
  }
  return fallback;
}

// Format numbers with appropriate suffixes and handle edge cases
function formatNumber(value, options = {}) {
  const num = safeNumber(value);
  const { prefix = "", allowNegative = false, fallback = "-" } = options;

  if (num === 0 && !options.showZero) return fallback;
  if (num < 0 && !allowNegative) return fallback;

  const absNum = Math.abs(num);
  let formatted;

  if (absNum >= 1_000_000_000) {
    formatted = (absNum / 1_000_000_000).toFixed(absNum >= 10_000_000_000 ? 0 : 1) + "B";
  } else if (absNum >= 1_000_000) {
    formatted = (absNum / 1_000_000).toFixed(absNum >= 10_000_000 ? 0 : 1) + "M";
  } else if (absNum >= 1_000) {
    formatted = (absNum / 1_000).toFixed(absNum >= 10_000 ? 0 : 1) + "K";
  } else {
    formatted = Math.round(absNum).toString();
  }

  // Remove trailing .0
  formatted = formatted.replace(/\.0([KMB])$/, "$1");

  const sign = num < 0 ? "-" : prefix;
  return sign + formatted;
}

// Get size class based on string length
function getHeroSizeClass(value) {
  const len = value.length;
  if (len <= 4) return "size-lg";
  if (len <= 6) return "size-md";
  if (len <= 8) return "size-sm";
  return "size-xs";
}

// Get peak time display text
function getPeakTimeText(data) {
  const peak = data.peakPeriod;
  if (!peak) return "";

  const lower = peak.toLowerCase();
  if (lower.includes("night")) return "Night owl mode";
  if (lower.includes("evening")) return "Evening coder";
  if (lower.includes("morning")) return "Early bird";
  if (lower.includes("afternoon")) return "Afternoon grinder";
  return "";
}

// Get top language
function getTopLanguage(data) {
  const langs = data.languages;
  if (!langs || !Array.isArray(langs) || langs.length === 0) return "-";

  const sorted = [...langs].sort((a, b) => {
    const aVal = safeNumber(a.percentage) || safeNumber(a.lines);
    const bVal = safeNumber(b.percentage) || safeNumber(b.lines);
    return bVal - aVal;
  });

  const top = sorted[0];
  if (!top || !top.name) return "-";

  // Shorten common language names
  const nameMap = {
    typescript: "TS",
    javascript: "JS",
    python: "Py",
    markdown: "MD",
    rust: "Rust",
    golang: "Go",
    go: "Go"
  };

  return nameMap[top.name.toLowerCase()] || top.name.slice(0, 4);
}

// Sanitize roast text - remove em dashes and clean up
function sanitizeRoast(text) {
  if (!text || typeof text !== "string") return "You are doing great... probably.";

  return text
    .replace(/—/g, "-")           // Replace em dashes with hyphens
    .replace(/–/g, "-")           // Replace en dashes with hyphens
    .replace(/,\s*and\s/gi, " and ")  // Remove Oxford comma
    .replace(/,\s*or\s/gi, " or ")    // Remove Oxford comma variant
    .replace(/\s+/g, " ")         // Normalize whitespace
    .trim()
    .slice(0, 200);               // Limit length
}

export async function renderCard(data, outputPath) {
  ensurePlaywrightCachePath();
  const templatePath = path.join(__dirname, "..", "templates", "insights-card.html");
  const cssPath = path.join(__dirname, "..", "templates", "insights-card.css");
  const [template, css] = await Promise.all([
    fs.readFile(templatePath, "utf8"),
    fs.readFile(cssPath, "utf8")
  ]);

  const html = applyTemplate(template, buildTemplateData(data, css));

  await withChromium(async () => {
    const { chromium } = await loadPlaywright();
    const browser = await chromium.launch();
    try {
      const page = await browser.newPage({ viewport: { width: 800, height: 640 } });
      await page.setContent(html, { waitUntil: "load" });
      await page.screenshot({ path: outputPath, type: "png" });
    } finally {
      await browser.close();
    }
  });
}

function buildTemplateData(data, css) {
  // Safely extract values with fallbacks
  const hours = safeNumber(data.totals?.hours);
  const messages = safeNumber(data.totals?.messages);
  const sessions = safeNumber(data.totals?.sessions);
  const linesAdded = safeNumber(data.lines?.added);
  const linesRemoved = safeNumber(data.lines?.removed);
  const msgsPerDay = safeNumber(data.messagesPerDay);

  // Determine hero value - prefer hours if available
  let heroValue, heroLabel;
  if (hours > 0) {
    heroValue = Math.round(hours) + "h";
    heroLabel = "hours with Claude";
  } else if (messages > 0) {
    heroValue = formatNumber(messages, { showZero: true });
    heroLabel = "messages sent";
  } else if (sessions > 0) {
    heroValue = formatNumber(sessions, { showZero: true });
    heroLabel = "sessions";
  } else {
    heroValue = "0";
    heroLabel = "stats available";
  }

  // Format date range safely
  let dateRange = "";
  try {
    if (data.dateRange?.start && data.dateRange?.end) {
      dateRange = formatDateRange(data.dateRange.start, data.dateRange.end);
    }
  } catch {
    dateRange = "";
  }

  // Get funny title
  const funnyTitle = getFunnyTitle(data);

  // Get roast text - generate a funny coding joke based on stats
  const roast = generateRoast(data);

  // Shame stats
  const dissatisfied = safeNumber(data.satisfaction?.find(s => s.name?.toLowerCase().includes("dissatisfied"))?.count);
  const frictionCount = safeNumber(data.frictionCount);
  const achievementRate = safeNumber(data.achievementRate, 1);
  const undoRate = achievementRate < 1 ? Math.round((1 - achievementRate) * 100) + "%" : "-";

  return {
    STYLE: css,
    HERO_VALUE: escapeHtml(heroValue),
    HERO_SIZE_CLASS: getHeroSizeClass(heroValue),
    HERO_LABEL: escapeHtml(heroLabel),
    SESSIONS: escapeHtml(formatNumber(sessions)),
    LINES_ADDED: escapeHtml(formatNumber(linesAdded, { prefix: "+" })),
    LINES_REMOVED: escapeHtml(formatNumber(linesRemoved, { prefix: "-" })),
    DATE_RANGE: escapeHtml(dateRange || "All time"),
    MSGS_PER_DAY: escapeHtml(formatNumber(msgsPerDay, { showZero: true, fallback: "0" })),
    TOP_LANG: escapeHtml(getTopLanguage(data)),
    PEAK_TIME: escapeHtml(getPeakTimeText(data)),
    USER_TITLE: escapeHtml(funnyTitle.title),
    TITLE_REASON: escapeHtml(funnyTitle.reason),
    ROAST: escapeHtml(roast),
    DISSATISFIED: escapeHtml(formatNumber(dissatisfied, { fallback: "-" })),
    FRICTION: escapeHtml(formatNumber(frictionCount, { fallback: "-" })),
    UNDO_RATE: escapeHtml(undoRate)
  };
}

function applyTemplate(template, replacements) {
  let output = template;
  for (const [key, value] of Object.entries(replacements)) {
    output = output.replaceAll(`{{${key}}}`, value ?? "");
  }
  return output;
}

function ensurePlaywrightCachePath() {
  if (!process.env.PLAYWRIGHT_BROWSERS_PATH || process.env.PLAYWRIGHT_BROWSERS_PATH === "0") {
    process.env.PLAYWRIGHT_BROWSERS_PATH = getPlaywrightCacheDir();
  }
}

async function loadPlaywright() {
  if (!playwrightLoadPromise) {
    playwrightLoadPromise = importPlaywright();
  }
  return playwrightLoadPromise;
}

async function importPlaywright() {
  try {
    return await import("playwright");
  } catch (error) {
    if (isMissingPlaywright(error) && !attemptedPlaywrightInstall) {
      attemptedPlaywrightInstall = true;
      await installPlaywrightPackage();
      return await import("playwright");
    }
    throw error;
  }
}

function isMissingPlaywright(error) {
  if (!error || typeof error !== "object") {
    return false;
  }
  if (error.code !== "ERR_MODULE_NOT_FOUND") {
    return false;
  }
  const message = typeof error.message === "string" ? error.message : "";
  return message.includes("playwright");
}

async function withChromium(fn) {
  try {
    return await fn();
  } catch (error) {
    if (isMissingBrowserError(error)) {
      await installChromium();
      return await fn();
    }
    throw error;
  }
}

function isMissingBrowserError(error) {
  const message = error instanceof Error ? error.message : String(error);
  return (
    message.includes("browserType.launch") &&
    (message.includes("Executable doesn't exist") || message.includes("chromium"))
  );
}

async function installChromium() {
  const cliPath = resolvePlaywrightCli();
  const env = { ...process.env };
  await fs.mkdir(env.PLAYWRIGHT_BROWSERS_PATH, { recursive: true });
  await runSilent(cliPath, ["install", "chromium"], env);
}

function resolvePlaywrightCli() {
  const binName = process.platform === "win32" ? "playwright.cmd" : "playwright";
  const candidate = path.join(__dirname, "..", "node_modules", ".bin", binName);
  if (fsSync.existsSync(candidate)) {
    return candidate;
  }
  return binName;
}

function runSilent(cmd, args, env) {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, {
      env,
      windowsHide: true,
      stdio: "ignore",
      shell: process.platform === "win32"
    });
    child.on("error", reject);
    child.on("exit", (code) => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`Playwright install failed with exit code ${code}`));
      }
    });
  });
}

async function installPlaywrightPackage() {
  if (isEnvDisabled(process.env.COSTATS_NO_INSTALL)) {
    throw new Error(
      "Playwright is not installed. Run `pnpm install --prod` or `npm install --omit=dev` in tools/insights-cli."
    );
  }
  const pm = detectPackageManager();
  const hasPnpmLock = fsSync.existsSync(path.join(projectRoot, "pnpm-lock.yaml"));
  const args =
    pm === "pnpm"
      ? ["install", "--prod", hasPnpmLock ? "--frozen-lockfile" : "--prefer-frozen-lockfile"]
      : ["install", "--omit=dev", "--no-fund", "--no-audit"];
  const env = { ...process.env, PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD: "1" };
  const result =
    pm === "pnpm"
      ? spawnSync(resolvePmCommand("pnpm"), args, { cwd: projectRoot, stdio: "inherit", env })
      : runNpm(args, { cwd: projectRoot, stdio: "inherit", env });
  if (result.error) {
    const toolName = pm === "pnpm" ? "pnpm" : "npm";
    throw new Error(`Failed to run ${toolName}. Is it installed and on PATH?`);
  }
  if (result.status !== 0) {
    throw new Error(`Playwright install failed (exit code ${result.status}).`);
  }
}

function detectPackageManager() {
  const pnpmCmd = resolvePmCommand("pnpm");
  if (commandAvailable(pnpmCmd)) {
    return "pnpm";
  }
  if (isNpmAvailable()) {
    return "npm";
  }
  throw new Error("No npm or pnpm found. Install Node.js (includes npm) or install pnpm.");
}

function resolvePmCommand(pm) {
  if (process.platform === "win32") {
    return `${pm}.cmd`;
  }
  return pm;
}

function commandAvailable(cmd) {
  const check = spawnSync(cmd, ["-v"], { stdio: "ignore" });
  return !check.error && check.status === 0;
}

function isNpmAvailable() {
  const npmExecPath = process.env.npm_execpath;
  if (npmExecPath && fsSync.existsSync(npmExecPath)) {
    return true;
  }
  const npmCmd = resolvePmCommand("npm");
  return commandAvailable(npmCmd);
}

function runNpm(args, options) {
  const npmExecPath = process.env.npm_execpath;
  if (npmExecPath && fsSync.existsSync(npmExecPath)) {
    return spawnSync(process.execPath, [npmExecPath, ...args], options);
  }
  const npmCmd = resolvePmCommand("npm");
  return spawnSync(npmCmd, args, options);
}
function isEnvDisabled(value) {
  if (!value) return false;
  const normalized = String(value).trim().toLowerCase();
  return normalized === "1" || normalized === "true" || normalized === "yes";
}
