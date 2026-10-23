# shellcheck shell=bash
# Prints the repository-relative directory of each TypeScript AppHost: a tracked aspire.config.json
# whose AppHost language is TypeScript. Source this file; it defines functions only.
typescript_apphost_directories() {
  local repository_root="$1"
  local config=""

  while IFS= read -r config; do
    if grep -qE '"language"[[:space:]]*:[[:space:]]*"typescript' "$repository_root/$config"; then
      dirname "$config"
    fi
  done < <(git -C "$repository_root" ls-files -- '*aspire.config.json')
}

# Prints "<name> <version>" for each runtime dependency in a package.json.
package_runtime_dependencies() {
  node -e '
    const dependencies = require(process.argv[1]).dependencies ?? {};
    for (const [name, version] of Object.entries(dependencies)) console.log(`${name} ${version}`);
  ' "$1"
}
