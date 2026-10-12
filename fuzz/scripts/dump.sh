#!/usr/bin/env bash
#
# Dumps the fuzz databases twice with camus-dump: once per database, and once with --all-databases.
# The dump options come from `dump-plan`, so each seed tests a different mix of them.
#
# usage: dump.sh <generate-report.json> <seed> <out-dir>
#
# Environment:
#   CAMUS_DUMP   the camus-dump command (default: camus-dump)
#   CAMUS_REST   the REST endpoint of the source server (default: http://localhost:5095)
#   CAMUS_GRPC   the gRPC endpoint of the source server (default: http://localhost:5096)

set -euo pipefail

report=$1
seed=$2
out=$3

fuzz="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dump=${CAMUS_DUMP:-camus-dump}
rest=${CAMUS_REST:-http://localhost:5095}
grpc=${CAMUS_GRPC:-http://localhost:5096}

eval "$(node "$fuzz/dist/cli.js" dump-plan --seed "$seed" --rest "$rest" --grpc "$grpc")"

mkdir -p "$out/per-database"
{
  echo "per-database:  $PER_DB_ARGS --create-database (.$PER_DB_EXT)"
  echo "all-databases: $ALL_DB_ARGS -A (.$ALL_DB_EXT)"
} | tee "$out/dump-plan.txt"

databases=$(node -e 'for (const d of require(process.argv[1]).databases) console.log(d.name)' "$report")

for database in $databases; do
  echo "dumping $database"
  # shellcheck disable=SC2086 # the option lists are meant to split into words
  $dump $PER_DB_ARGS --create-database -d "$database" -o "$out/per-database/$database.$PER_DB_EXT" \
    2>> "$out/dump-stderr.log"
done

echo "dumping every database"
# shellcheck disable=SC2086
$dump $ALL_DB_ARGS -A -o "$out/all-databases.$ALL_DB_EXT" 2>> "$out/dump-stderr.log"

if [[ -s "$out/dump-stderr.log" ]]; then
  echo "camus-dump wrote to standard error:"
  cat "$out/dump-stderr.log"
fi
