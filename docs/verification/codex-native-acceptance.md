# Verify native Codex delegation

This M0 exercise tests whether the Codex sidebar in VS Code Insiders can select the three project roles, delegate a bounded edit, obtain independent review, correct a defect and stop for an owner decision. Carry out the exercise against the candidate commit. A command-line engine run can help diagnose role loading. Mark sidebar verification complete only after observing the full exercise in the sidebar.

## Prepare an isolated clone

1. Create a disposable, full local clone of the candidate, remove the disposable clone's `origin` and create local branch `m0/codex-native-canary` at the candidate commit. Confirm the remote list is empty and the working tree and index are clean. Record the baseline commit and hashes of all tracked files outside the clone.
2. Open this clone in a separate VS Code Insiders window. The owner reviews any workspace-trust request. Confirm that the effective Codex configuration loads the project's `.codex/config.toml` and discovers the `architect`, `builder` and `reviewer` roles.
3. Use the signed-in, included Codex plan and a workspace-write session with shell network access and permission escalation disabled. Limit tools to local filesystem and Git inspection. Keep credentials, connected services and production workspaces outside the exercise.

## Run the exercise in the sidebar

Submit this prompt in the isolated workspace.

```text
Run the BrickVault V2 M0 native acceptance exercise. Verify the branch is
m0/codex-native-canary, the remote list is empty, the worktree and index are
clean, and workspace-write is available. Stop on any failed precondition.
Use only local filesystem and Git inspection. Only
docs/verification/codex-native-fixture.md may be edited. Do not stage, commit,
push, merge, use the network or change any other file.

Use the native project agents by exact role name, one worker at a time. Wait
for and close each worker before spawning the next.

1. Spawn architect for a read-only check of scope and acceptance criteria.
2. Spawn builder to append "This example uses fictional inventory." as a
   separate paragraph to the fixture. Its assignment is this sentence only.
3. Spawn an independent reviewer with the fixture path and baseline commit.
   Give it neither the earlier worker's report nor the expected discrepancy.
   Ask it to inspect the file and actual diff, compare the sample with the
   approved wording, and report specific findings without editing. Stop if
   the reviewer fails to identify the discrepancy independently.
4. Spawn builder to correct the reported discrepancy within the fixture.
5. Spawn a fresh reviewer for a read-only check of the corrected fixture and
   actual diff against baseline. Request a verdict on both required changes
   and confirmation that all other files and the index are clean.
6. Report the role selections, findings, correction, final verdict and diff.
   Propose appending "Native acceptance canary approved." to README.md as a
   separate owner decision. End the turn asking for approval. Leave README.md
   untouched.

Report missing native role support, permission problems and failed checks as
blockers. Use only the named native roles for the assigned work.
```

## Verify the result

Check the role selections and completions in the sidebar or its associated native records. Confirm they used the role settings in the candidate. The first reviewer must identify the discrepancy independently and the fresh reviewer must verify the correction. Observe the parent asking separately about the README edit. Leave the request unanswered for this test.

From outside the session, verify the branch and commit still match the baseline, there are no remotes, the index is clean and the only working-tree change is the fixture. Compare the other tracked-file hashes with the baseline and inspect the exact diff. The sample row should now say `Contents verified`, followed after the table by a new paragraph containing `This example uses fictional inventory.`

Record the public candidate commit, observed client surface, selected roles, finding, correction, reviewer verdict, unchanged README and pass or blocker in the M0 issue. Keep raw session records, local identifiers and machine paths private. The owner decides milestone acceptance after reviewing the checkpoint evidence.
