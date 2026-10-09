import { execFile } from "node:child_process";
import path from "node:path";
import { promisify } from "node:util";
import { analyzeCommits as analyzeConventionalCommits } from "@semantic-release/commit-analyzer";
import { generateNotes as generateConventionalNotes } from "@semantic-release/release-notes-generator";

const execFileAsync = promisify(execFile);
const extensionsDirectory = "extensions";

function normalizePath(filePath) {
  return filePath.replaceAll("\\", "/").replace(/^\.\//, "").replace(/\/$/, "");
}

export function isWithinDirectory(filePath, directory) {
  return normalizePath(filePath).startsWith(`${normalizePath(directory)}/`);
}

// Maps extensions/Shirubasoft.Aspire.Extensions.CloudflareTunnels to cloudflare-tunnels.
export function extensionScope(extensionPath) {
  return path.posix.basename(normalizePath(extensionPath))
    .split(".")
    .at(-1)
    .replace(/(?<=[a-z0-9])(?=[A-Z])/gu, "-")
    .toLowerCase();
}

export function commitScope(message) {
  return /^\w+\((?<scope>[^()\r\n]+)\)!?:/u.exec(message)?.groups.scope.trim();
}

async function readCommitFiles(hash, cwd) {
  const { stdout } = await execFileAsync(
    "git",
    ["diff-tree", "--root", "--no-commit-id", "--name-only", "-r", hash],
    { cwd },
  );
  return stdout.split(/\r?\n/u).filter(Boolean);
}

async function readExtensionPaths(cwd) {
  const { stdout } = await execFileAsync(
    "git", ["ls-tree", "-d", "--name-only", "HEAD:extensions"], { cwd },
  );
  return stdout.split(/\r?\n/u).filter(Boolean).map((name) => `${extensionsDirectory}/${name}`);
}

// A commit belongs to an extension when it touches the extension folder. A change to a shared
// input belongs to every extension unless the commit's scope names a different extension.
export async function filterExtensionCommits(
  commits,
  { extensionPath, sharedPaths },
  { cwd = process.cwd(), resolveFiles = readCommitFiles, resolveExtensionPaths = readExtensionPaths } = {},
) {
  if (!extensionPath || !Array.isArray(sharedPaths)) {
    throw new TypeError("extension release analysis requires extensionPath and sharedPaths.");
  }

  const ownScope = extensionScope(extensionPath);
  const otherScopes = new Set(
    (await resolveExtensionPaths(cwd)).map(extensionScope).filter((scope) => scope !== ownScope),
  );
  const shared = new Set(sharedPaths.map(normalizePath));

  const extensionCommits = [];
  for (const commit of commits) {
    const files = await resolveFiles(commit.hash, cwd);
    const touchesExtension = files.some((file) => isWithinDirectory(file, extensionPath));
    const touchesSharedInput = files.some((file) => shared.has(normalizePath(file)));
    if (touchesExtension || (touchesSharedInput && !otherScopes.has(commitScope(commit.message)))) {
      extensionCommits.push(commit);
    }
  }
  return extensionCommits;
}

async function scopedContext(pluginConfig, context) {
  const { extensionPath, sharedPaths, ...delegateConfig } = pluginConfig;
  const commits = await filterExtensionCommits(
    context.commits,
    { extensionPath, sharedPaths },
    { cwd: context.cwd },
  );
  return { delegateConfig, context: { ...context, commits } };
}

export async function analyzeCommits(pluginConfig, context) {
  const scoped = await scopedContext(pluginConfig, context);
  if (scoped.context.commits.length === 0) {
    return null;
  }

  return analyzeConventionalCommits(scoped.delegateConfig, scoped.context);
}

export async function generateNotes(pluginConfig, context) {
  const scoped = await scopedContext(pluginConfig, context);
  return generateConventionalNotes(scoped.delegateConfig, scoped.context);
}
