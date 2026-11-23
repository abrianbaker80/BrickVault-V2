# BrickVault V2 agent instructions

Work in the independent BrickVault V2 repository. Before editing or using GitHub, verify the repository root, branch, origin, index and existing changes. Preserve unrelated files and their history. Stage exact authorized paths and report any conflicting or residual work.

## Scope and authority

Read the [master plan](docs/architecture/MASTER_PLAN.md), [feature matrix](docs/architecture/FEATURE_MATRIX.md) and [decision register](docs/architecture/DECISIONS.md). The owner accepted M0 and authorized the isolated M1 design and framework comparison. The owner accepts milestones and authorizes later milestones, consequential product or accounting decisions, major visual design, paid features and changes to protected controls. Escalate a decision with the recommendation, alternatives, consequences, affected requirements and exact action awaiting approval.

Keep V1, its data, infrastructure, hardware and installed application outside the V2 build and test environment. Production deployment, migrations and marketplace writes need separate authorization. Use fictional examples in public materials. Keep credentials, customer records, real store figures, local paths and private session traces out of files, issues, PRs and logs.

Install missing required tools in the intended environment. Diagnose and correct the root cause of a failure and verify the change through the normal workflow. If a workaround is necessary, explain the constraint and tradeoffs and obtain explicit owner approval first.

## Business rules

BrickVault owns the stock record and publishing decisions. Use one authorized BrickLink publisher. Reconcile uncertain writes or conflicting observations before publishing again. Track physical on-hand stock, reservations, unavailable stock, sellable stock and marketplace quantities separately. Reserve a valid order obligation. Picking and packing retain physical custody. Confirmed carrier handoff reduces both physical on-hand and the reservation. Verify the location and condition of canceled picked or packed stock before releasing it for sale.

Use identifiable receipt-layer costs wherever possible and documented FIFO for genuinely mixed units. Carry unknown cost explicitly and report cost-basis coverage. Preserve cost conservation and audit evidence through every stock movement. Feature acceptance requires a complete working workflow and its evidence.

## Delegation and review

Use the project roles defined in `.codex/`. The Architect assigns approved work, inspects the actual changes, reconciles evidence and brings owner decisions forward. The Builder implements a bounded assignment, verifies it and reports the exact diff or commit. An independent Reviewer examines the diff and requirements directly, reports findings and verifies each correction. The Builder cannot accept its own work. A shared GitHub login may record a review verdict with the reviewed commit and the reviewer's role.

Define the assignment's objective, writable paths, verification and stop condition. Limit concurrent spawned agents to two unless the owner approves a measured change. Run implementation and independent review sequentially in a shared worktree. Isolate overlapping edits and coordinate Git operations. Use the included Codex allowance and check available limits and billing when exposed. Pause before unapproved paid execution.

## Issues, protection and checkpoints

Manage routine authorized GitHub and documentation work. Open or reuse the milestone and issue, then link requirements, branch, pull request, reviewed commit, independent review, local checks and hosted CI. Keep the plan, matrix, decision register and changelog accurate.

Use feature branches and pull requests. Verify current required checks and the effective protections before any authorized merge. Keep `main` protected against direct pushes, deletion and force pushes. Treat a failed or unenforceable gate as an open checkpoint requirement. Preserve the candidate and describe what blocks it. Use the [M0 setup](README.md#m0-setup) for local verification.

After an authorized merge, fetch and reconcile the local, remote-tracking and GitHub `main` commits. Record the verified outcome and outstanding evidence. Keep the current issue and milestone open until owner acceptance. M2 requires separate authorization.
