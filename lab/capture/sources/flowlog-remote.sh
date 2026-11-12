#!/bin/sh
# Runs inside the reader pod (see flowlogs.sh). $1 is the storage account; the user's Entra token arrives on stdin.
# The token reaches curl through curl's config on stdin, so it is in no process argument and in no file.
# Prints each blob as `#BLOB <name>`, the blob, `#END`.
set -u
read -r T
A="$1"; C=insights-logs-flowlogflowevent; V="x-ms-version: 2023-11-03"; marker=""
names="${TMPDIR:-/tmp}/names"
get() { printf 'header = "Authorization: Bearer %s"\n' "$T" | curl -fsS -K - -H "$V" "$@"; }
while :; do
  xml=$(get "https://$A.blob.core.windows.net/$C?restype=container&comp=list&maxresults=500${marker:+&marker=$marker}") || exit 2
  echo "$xml" | grep -o '<Name>[^<]*</Name>' | sed 's/<[^>]*>//g' > "$names"
  while read -r n; do
    printf '#BLOB %s\n' "$n"
    get "https://$A.blob.core.windows.net/$C/$n"
    printf '\n#END\n'
  done < "$names"
  marker=$(echo "$xml" | sed -n 's/.*<NextMarker>\([^<]*\)<\/NextMarker>.*/\1/p')
  [ -n "$marker" ] || break
done
