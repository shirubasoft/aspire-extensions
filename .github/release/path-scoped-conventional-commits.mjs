import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { analyzeCommits as analyzeConventionalCommits } from "@semantic-release/commit-analyzer";
import { generateNotes as generateConventionalNotes } from "@semantic-release/release-notes-generator";

const execFileAsync = promisify(execFile);

function normalizePath(path) {
  return path.replaceAll("\\", "/").replace(/^\.\//, "").replace(/\/$/, "");
}

export function pathMatchesScope(path, scopes) {
  const candidate = normalizePath(path);
  return scopes.some((scope) => {
    const normalizedScope = normalizePath(scope);
    if (normalizedScope.endsWith("/**")) {
      const directory = normalizedScope.slice(0, -3);
      return candidate === directory || candidate.startsWith(`${directory}/`);
    }

    return candidate === normalizedScope;
  });
}

async function readCommitFiles(hash, cwd) {
  const { stdout } = await execFileAsync(
    "git",
    ["diff-tree", "--root", "--no-commit-id", "--name-only", "-r", hash],
    { cwd },
  );
  return stdout.split(/\r?\n/u).filter(Boolean);
}

export async function filterCommitsByPath(
  commits,
  scopes,
  { cwd = process.cwd(), resolveFiles = readCommitFiles } = {},
) {
  if (!Array.isArray(scopes) || scopes.length === 0) {
    throw new TypeError("path-scoped release analysis requires at least one path.");
  }

  const scopedCommits = [];
  for (const commit of commits) {
    const files = await resolveFiles(commit.hash, cwd);
    if (files.some((file) => pathMatchesScope(file, scopes))) {
      scopedCommits.push(commit);
    }
  }
  return scopedCommits;
}

async function scopedContext(pluginConfig, context) {
  const { paths, ...delegateConfig } = pluginConfig;
  const commits = await filterCommitsByPath(context.commits, paths, { cwd: context.cwd });
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
