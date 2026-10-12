#!/usr/bin/env bash
#
# Loads dump files into a server with camus-cli, in the order given. A .gz file is decompressed on
# the way in. The first statement that fails stops the load and fails the script.
#
# usage: restore.sh <file>...
#
# Environment:
#   CAMUS_CLI        the camus-cli command (default: camus-cli)
#   RESTORE_ENDPOINT the endpoint to load into (default: http://localhost:5096)

set -euo pipefail

cli=${CAMUS_CLI:-camus-cli}
endpoint=${RESTORE_ENDPOINT:-http://localhost:5096}

for file in "$@"; do
  echo "restoring $file"
  if [[ "$file" == *.gz ]]; then
    # shellcheck disable=SC2086 # CAMUS_CLI may be a command with arguments
    gunzip -c "$file" | $cli -c "Endpoint=$endpoint" --no-history -f -
  else
    # shellcheck disable=SC2086
    $cli -c "Endpoint=$endpoint" --no-history -f "$file"
  fi
done
