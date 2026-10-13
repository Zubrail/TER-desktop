#!/bin/bash
# live_fake.sh "<tour>" [VAR=val...] : start fake backend, run a test-instance tour against it, stop the backend.
HERE="$(cd "$(dirname "$0")/../.." && pwd)"
PORT=${FAKE_PORT:-18080}
env "${@:2}" python3 "$HERE/termitus/tools/fake_backend.py" $PORT & FP=$!
sleep 1
"$HERE/termitus/tools_live.sh" "$1" TER_TEST_IMPORT_URL=http://127.0.0.1:$PORT/subscription/TESTTOKEN "${@:2}"
kill $FP
