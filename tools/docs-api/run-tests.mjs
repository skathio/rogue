// Lets `node --test tools/docs-api/` run from the repo root. Node 21 and later take a folder
// argument as one file to run and load it through package.json's `main`, which is this file: it
// runs every *.test.mjs file here and passes the result on. The files run one at a time, because
// two of them restore the solution and run docfx, and two restores of the same projects at once
// can fail on a file the other holds open.
import { spawnSync } from 'node:child_process'

// The outer runner marks its child with NODE_TEST_CONTEXT; left set, the inner runner would
// write its results in the outer runner's wire format instead of running as a suite of its own.
const { NODE_TEST_CONTEXT: _, ...env } = process.env
const run = spawnSync(process.execPath, ['--test', '--test-concurrency=1'], { cwd: import.meta.dirname, env, stdio: 'inherit' })
process.exit(run.status ?? 1)
