# Verify native Codex delegation

This M0 exercise tests whether the Codex sidebar in VS Code Insiders can select the three project roles, delegate a bounded edit, obtain independent review, correct a defect and stop for an owner decision. Carry out the exercise against the candidate commit. A command-line engine run can help diagnose role loading. Mark sidebar verification complete only after observing the full exercise in the sidebar.

## One-time preparation by the coordinator

1. Create a disposable, full local clone of the candidate, remove the disposable clone's `origin` and create local branch `m0/codex-native-canary` at the candidate commit. Confirm the remote list is empty and the working tree and index are clean. Record the baseline commit and hashes of all tracked files outside the clone.
2. Open the disposable clone's `docs/verification` directory as the workspace folder in a separate VS Code Insiders window. The owner reviews any workspace-trust request. From this directory Codex loads the repository's native role definitions and the verification directory's scoped `.codex/config.toml`. The scoped profile disables shell network access, permission escalation, and memory use and generation. Use the signed-in, included Codex plan.
3. In that window's Command Palette, choose **Codex: New Chat in ChatGPT Sidebar**. If a composer permissions preset overrides the scoped configuration, select **Custom (config.toml)**. Submit the line below in that fresh chat. Full access fails the permission check.

```text
You are the parent in this existing chat. Before reading any file, verify from the effective session context that workspace-write is active, shell network access and permission escalation are disabled, and no active instruction directs an external memory, skill or history lookup. If any condition fails or cannot be verified, report it and stop. Otherwise execute “In-chat exercise” in codex-native-acceptance.md here now with native project roles. Keep all parent and child reads inside this clone.
```

## In-chat exercise

The parent in the active sidebar chat executes these instructions directly. Do not open another chat, dispatch another client, control the UI through Computer Use or any other tool, or request desktop permissions. Use only this chat's native project subagents, local filesystem access in the clone and Git inspection. Give every child the same isolation instructions. Each child must stop before reading files if its effective permissions or instructions conflict with the preconditions. Neither parent nor children may look up memories, skills, prior chats, session logs or history outside the clone.

Start in the verification workspace. Use `git rev-parse --show-toplevel` to resolve the clone root. Confirm that the current directory is its `docs/verification` folder, the branch is `m0/codex-native-canary`, the remote list is empty, and the entire worktree and index are clean. Record the current commit as the baseline. Stop on a failed precondition and report which check failed. Only `codex-native-fixture.md` in the current directory may be edited. Do not stage, commit, push, merge, use the network or change any other file.

Use this chat's native tool to select each project role by its exact name. Keep one worker active at a time. For each dispatch, record the selected role and its native completion receipt. Advance only after the tool confirms successful terminal completion and the worker returns the requested result. An interim message or wait timeout requires checking that worker's status again. If the native selector is missing, a dispatch exceeds available capacity or a worker fails, stop and report the step, role and observed blocker.

1. Spawn architect for a read-only check of scope and acceptance criteria.
2. Spawn builder to append "This example uses fictional inventory." as a
   separate paragraph to the fixture. Its assignment is this sentence only.
3. Spawn an independent reviewer with a fresh task context containing the fixture path and baseline commit.
   Keep the earlier worker's report, the coordinator's guide and the expected discrepancy out of its brief and inherited chat history.
   Ask it to inspect the file and actual diff, compare the sample with the
   approved wording, and report specific findings without editing. Stop if
   the reviewer fails to identify the discrepancy independently.
4. Spawn builder to correct the reported discrepancy within the fixture.
5. Spawn a fresh reviewer for a read-only check of the corrected fixture and
   actual diff against baseline. Request a verdict on both required changes
   and confirmation that all other files and the index are clean.
6. Report the role selections, findings, correction, final verdict and diff.
   Propose appending "Native acceptance canary approved." to the repository-root
   `README.md` as a separate owner decision. End the turn asking for approval.
   Leave that file untouched.

## Independent verification by the coordinator

Check the role selections and completions in the sidebar or its associated native records. Confirm they used the role settings in the candidate. The first reviewer must identify the discrepancy independently and the fresh reviewer must verify the correction. Observe the parent asking separately about the README edit. Leave the request unanswered for this test.

From outside the session, verify the branch and commit still match the baseline, there are no remotes, the index is clean and the only working-tree change is the fixture. Compare the other tracked-file hashes with the baseline and inspect the exact diff. The sample row should now say `Contents verified`, followed after the table by a new paragraph containing `This example uses fictional inventory.`

Record the public candidate commit, observed client surface, selected roles, finding, correction, reviewer verdict, unchanged README and pass or blocker in the M0 issue. Keep raw session records, local identifiers and machine paths private. The owner decides milestone acceptance after reviewing the checkpoint evidence.
