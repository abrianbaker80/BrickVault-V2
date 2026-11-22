# BrickVault V2 decision register

The owner approved the decisions below for architecture planning and authorized M0 implementation. Each future milestone, live provider qualification, deployment and migration has a separate gate. [Master plan](MASTER_PLAN.md) · [Feature matrix](FEATURE_MATRIX.md)

| ID | Owner decision | Operating rule and next gate |
|---|---|---|
| D01 · Approved | Layered physical stock and reservations. | An authoritative BrickLink order/amendment creates the obligation. Reserve available units once. Picking and packing retain physical custody. On confirmed carrier handoff reduce both on-hand and backed reservation. On cancellation, picked/packed units require verified condition and physical location before resale. Preserve separate physical, reserved, unavailable, sellable, desired and observed marketplace quantities. A marketplace sale causes no second deduction. |
| D02 · Approved | One V2 publisher, durable intents/receipts, polling and reconciliation. V2 owns stock; BrickLink originates marketplace sales. | After specific qualification and live authorization, routine inventory and price changes run automatically. Block and escalate uncertain writes, conflicting evidence, unexplained stock movement or potentially destructive operations. V1 synchronization and live writes are outside M0. |
| D03 · Amended | Milestone structure approved. Initial release scope remains provisional until the owner reviews V1-dependent outcomes and approves their disposition. | Review end-to-end set part-out/purchasing/batches and one-click candidate, barcode identification, direct and specialty labels, Build Queue/projects/holds, multi-order picking and any additional capabilities the owner uses. For each, record outcome, current use/evidence, equivalent workflow, initial-release/defer recommendation and the owner's decision. Preserve continuity through migration. No placeholder can be accepted. Resolve before freezing release requirements; this review is not a prerequisite for M0. |
| D04 · Conditional | Always-on Proxmox hosting, browser access and ASP.NET Core/C# with PostgreSQL are the approved directions. Modern supported versions require documented justification. | Validate dependencies, transaction semantics, restore, hosting and compatibility before adoption. Before provisioning, review measured capacity, network, security isolation, backup and operating cost. The owner approved the design of a bounded M1 React/TS vs Vue/TS comparison using identical task, data, visual requirements and tests. Score usability, visual quality, maintainability, performance, accessibility and development efficiency. React pilot is a benchmark. The owner selects the frontend after seeing results. |
| D05 · Approved with clarifications | Use identifiable purchase/receipt-layer basis. For genuinely indistinguishable mixtures, consistently apply documented FIFO. | A cost-flow assignment does not prove which physical unit was shipped. Preserve costs, freight, discounts, allocation calculations and rounding evidence; conserve value through receiving, split/part-out, sale, return and adjustment. Carry unknown separately from zero and show basis coverage in profit reports. Separate internal management costing from tax accounting; refer consequential exceptions to the owner. |
| D06 · Amended | Publish the software repository from fresh, reviewed history. Retain the original history and supporting evidence privately. The runtime and store data remain private. M0 only. | Read back public visibility. Require a pull request and successful hosted check on main, enforce the rule for administrators, block force pushes and deletions, and record independent review of the actual diff. The owner accepts M0 and authorizes any further milestone separately. |
| D07 · Approved | Two distinct, polished visual directions using the logo and Bricqer’s compact organization and clarity as references. | Demonstrate equivalent realistic tasks in both directions. Independently compare framework ergonomics on a common design so visual preference cannot masquerade as technology evidence. The owner retains final approval of navigation, visual design and consequential workflows. Exploration begins only after separate M1 authorization. |

Privacy/retention, promoted devices, unresolved costs, test seller or live trial scope, cutover window, recovery targets and production operations retain their own gates. Prior alternatives and scoring below explain how the recommendations were reached. Implementation status is maintained in the matrix.

## Technology selection

Scores express design judgments on a 1–5 scale using the owner's workload priorities. The weighted total is the sum of each score times its weight divided by 100. M1 compares the leading frontend options using measured tasks.

| Cohesive option | Domain 25 | Upgrades 20 | UI 20 | Windows/device 15 | Tooling 10 | Security/ops cost 10 | /5 |
|---|---:|---:|---:|---:|---:|---:|---:|
| React/TS web, ASP.NET Core, PostgreSQL, scoped Windows agent | 5 | 5 | 5 | 4 | 5 | 4 | 4.75 |
| Vue/TS web with the same server, DB and device boundary | 5 | 5 | 5 | 4 | 4 | 4 | 4.65 |
| WinUI/C# desktop, ASP.NET Core, PostgreSQL | 5 | 4 | 4 | 5 | 4 | 4 | 4.40 |
| Flutter/Dart desktop, FastAPI, PostgreSQL | 4 | 4 | 4 | 4 | 4 | 4 | 4.00 |
| Svelte/TS web, Go, PostgreSQL, limited device bridge | 4 | 4 | 4 | 3 | 4 | 5 | 3.95 |

### Independent component decisions

Weights change by layer to avoid evaluating a database on typography. Scores in the vector follow the stated order.

| Layer and weights | Candidate | Scores | /5 | Basis |
|---|---|---|---:|---|
| Frontend · dense accessible workflows 35, maintenance 25, devices 15, rendering/input 15, tools 10 | React + TS | 5,4,3,5,5 | 4.45 | Accessible collection primitives and browser test tooling; allow time to integrate selection/focus. |
| Same | Vue 3 + TS | 5,4,3,5,4 | 4.35 | Strong declarative components; verify table and selection behavior with the same operator tasks. |
| Same | Svelte 5 + TS | 4,4,3,5,4 | 4.00 | Lean compiled client; more integration of complex interaction cases in this plan. |
| Same | Flutter + Dart | 4,4,5,4,4 | 4.15 | Strong native plugins and real V1 implementation; native desktop focus and dense multi-window parity need qualification. |
| Same | WinUI 3 + C# | 4,4,5,4,3 | 4.05 | Direct Windows integration; separate deployment and a separate client for other platforms. |
| Backend · transaction ergonomics 35, longevity 25, worker/IO 15, deployment 15, tooling 10 | ASP.NET Core/C# | 5,5,5,4,5 | 4.85 | Decimal domain types, explicit transactions, hosted worker, shared Windows language. |
| Same | Go/net/http | 4,4,5,5,4 | 4.30 | Compact binary and concurrency; more explicit decimal, validation and contract work. |
| Same | Python/FastAPI | 4,4,4,5,4 | 4.15 | Typed HTTP validation and vision ecosystem; runtime type/transaction boundaries need discipline. |
| Same | TypeScript/Fastify | 4,4,4,4,5 | 4.10 | Shared client types; exact decimals and runtime input validation still need explicit modeling. |
| DB · integrity 35, concurrent workers 25, recovery/upgrades 20, deployment 10, control/cost 10 | PostgreSQL | 5,5,5,4,5 | 4.90 | Row locks, partial uniqueness, transactional outbox, decimals and PITR suit the inventory/worker races. |
| Same | SQL Server | 5,5,4,4,3 | 4.50 | Capable transactional engine; edition, licensing and Linux operational choices add cost. |
| Same | SQLite | 4,2,3,5,5 | 3.50 | Very simple for a local spool; one writer and different recovery model for shared authoritative stock. |
| Hosting · always-on 30, devices 25, portability 20, lifecycle 15, cost 10 | Web + server + scoped endpoint agent | 5,4,5,4,4 | 4.50 | Synchronizes with laptop asleep; native access for approved jobs. |
| Same | Tauri/WebView2 shell + server | 5,5,3,3,3 | 4.10 | Native desktop packaging and device commands; additional client/update surface. |
| Same | Laptop-only native | 2,5,2,4,4 | 3.25 | Works locally during network loss, but seller-order discovery depends on laptop uptime. |
| Same | Browser facilities alone + server | 5,2,5,5,5 | 4.25 | Start here for PDF printing and keyboard wedge input; device-specific protocols may need the agent. |

Sources for the capability comparisons include [React Aria Table](https://react-aria.adobe.com/Table), [Vue release policy](https://vuejs.org/about/releases), [Svelte’s compiler/reactivity release](https://svelte.dev/blog/svelte-5-is-alive), [Flutter desktop](https://docs.flutter.dev/platform-integration/desktop), [Windows developer platform](https://learn.microsoft.com/en-us/windows/apps/get-started/windows-developer-faq), [Tauri architecture](https://v2.tauri.app/concept/architecture/), [ASP.NET hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services), [Go support](https://go.dev/doc/security/policy), [PostgreSQL locks](https://www.postgresql.org/docs/18/explicit-locking.html), [SQL Server editions](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-editions-and-components-2025) and [SQLite isolation](https://www.sqlite.org/isolation.html).

**Close decision.** At M1 compare React and Vue using the same synthetic inventory register, one bounded session per candidate. Same content, 150% scaling, split placements, rapid keyboard search, multi-selection, stale response and inspector. Observe task errors/completion, focus, response latency, clipping, implementation friction and the owner's preference. Stop after that view; build out only the selected design. React's slight score advantage is too small to dictate a visual choice.

### Supported baselines and lifecycle

| Concern | Proposed selection and upgrade rules |
|---|---|
| Versions | Choose supported, patched runtime, database, driver, frontend and build-tool releases when their milestone starts. Pin compatible versions, document support windows and test migration and restore against the candidate. M0 pins its own verification tools. [Microsoft lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) · [PostgreSQL lifecycle](https://www.postgresql.org/support/versioning/) · [Npgsql compatibility](https://www.npgsql.org/doc/compatibility.html) · [React releases](https://react.dev/versions) · [Node.js releases](https://nodejs.org/en/about/previous-releases) |
| UI and state | OpenAPI-generated client, server-owned revisions, TanStack Query for cache/invalidation, component/form state for edits, URL for filters. Accessible primitives (React Aria if selected) and own tokens. Use semantic tables first, virtualize only after measured need. No commercial grid or global state manager needed for current scope. [React Aria](https://react-aria.adobe.com/Table) · [TanStack Query](https://tanstack.com/query/latest/docs/framework/react/overview) |
| Server | One modular deployable, explicit SQL/Npgsql and checked migrations, resident .NET worker + PostgreSQL outbox. Single active publisher per store. No infrastructure queue or service mesh. Separate a worker or computer-vision process only for an approved isolation/load need. [Npgsql](https://www.npgsql.org/) |
| Packaging | Reproducible SDK, packages, build inputs, image digests and artifact manifests. Dedicated supported Linux guest, supervised app and DB, separate identities, TLS entry, explicit forward migrations. Publish static UI from same origin. Retain previous binaries; backwards-compatible schema window for rollback. |
| Verification and observability | xUnit, actual disposable PostgreSQL, deterministic fake storefront and clock for faults, Playwright with keyboard and accessible names, physical workstation tests. Structured logs/traces with correlation IDs and redaction; count lag, stale observations, uncertain writes, failed jobs and restore age. Review risk-specific contracts on upgrades. |
| Maintenance | Monthly patch/dependency review, urgent review of relevant security releases, quarterly scrutiny of support windows and restore/upgrade rehearsal. Lockfiles, CI dependency/secret/license checks, tracked owners and deadlines. Test imports, migrations, ledger replay, adapter and primary screenshots on major upgrades. Preserve adapters and transport/domain separation to permit replacement without a rewrite. |
| Backup | Encrypted independent base backup plus archived WAL, off-host verified copy, recoverable keys/config and release artifacts. Restore into isolated network with publisher disabled, replay balances and reconcile external frontier. Propose RPO 15 minutes and RTO 4 hours; approval follows timed rehearsal. [PostgreSQL PITR](https://www.postgresql.org/docs/18/continuous-archiving.html) |

## Repository and agent governance

Use focused branches, worktrees, issues and milestones tied to requirement IDs. Public commits, fixtures, CI logs and review material must contain only material suitable for public release. Restricted evidence can be recorded by type and disposition. Store its contents in the private record. Launch the public history from a reviewed seed, then protect main, require the hosted verification check and a pull request, enforce protections for administrators and reject force pushes and deletions. Verify settings through a fresh read and check execution on the proposed revision. [GitHub branch protection](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)

The Architect owns the scope, decision register, acceptance evidence and checkpoint. The Builder owns the assigned implementation, scoped changes, focused tests, setup instructions and change notes. The independent Reviewer inspects the actual diff, evidence and outstanding requirements. Record the verdict with the proposed revision. Multiple agents using one GitHub account share its review identity. Distinct-account approval would require a separately eligible reviewer. The public repository uses a required pull request, hosted check and recorded independent technical review.

Give each assignment its requirements, allowed files, proof, stop conditions and dependencies. Use sequential Builder and Reviewer work for dependent changes. At most two spawned agents may handle independent bounded work. Keep external credentials and representative store data outside development. Check the execution route before dispatch. Obtain owner approval for changes in scope, authority, billing, visual direction, consequential domain policy, production access and new milestones. [Codex subagents](https://developers.openai.com/codex/subagents)

G04 remains open until a witness observes the harmless project-scoped Builder assignment, independent review, correction and approval pause in the Insiders sidebar. Separate engine-level evidence in the private record informs the exercise. Record the surface and what the public evidence actually establishes. For each accepted checkpoint, link requirement, issue, PR, reviewed commit, applicable test and read-back of local and remote main. The owner retains milestone acceptance.
