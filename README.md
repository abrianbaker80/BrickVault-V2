# BrickVault V2

BrickVault V2 is a planned inventory and fulfillment system for a LEGO resale operation. BrickVault will manage stock, reservations and publishing decisions. BrickLink will remain the sales storefront.

**Only M0 engineering setup is authorized.** The repository contains the architecture, project roles and checks for reviewing changes. Application development starts in a separately authorized milestone. The owner accepts each milestone.

## M0 setup

Start with Git and PowerShell 7.6 or newer on Windows x64 or Linux x64. On Windows, install missing prerequisites from a PowerShell terminal and open a fresh terminal after installation.

```powershell
winget install --id Microsoft.PowerShell --exact --source winget
winget install --id Git.Git --exact --source winget
```

Clone the public repository with its complete history.

```powershell
git clone https://github.com/abrianbaker80/BrickVault-V2.git
Set-Location BrickVault-V2
```

To validate an open pull request, switch to its candidate branch. Once the checkpoint merges, use `main`. Run both scripts from the repository root.

```powershell
pwsh -NoProfile -File ./scripts/Setup.ps1
pwsh -NoProfile -File ./scripts/Check.ps1
```

Setup downloads the pinned Gitleaks release and verifies the archive and executable checksums. The check requires a full clone with `origin/main`. It verifies required tracked files, checks relative Markdown file links, parses PowerShell scripts, checks Git diffs and scans commit history and staged changes for secrets with redacted findings. Stage the exact intended files before the final local check. GitHub Actions runs the same scripts on a standard GitHub-hosted Linux runner. Maintainers who manage issues or pull requests from the terminal also need an authenticated GitHub CLI.

## Project documents

| Document | What it governs |
|---|---|
| [Master plan](docs/architecture/MASTER_PLAN.md) | Milestones, dependencies and acceptance criteria |
| [Feature matrix](docs/architecture/FEATURE_MATRIX.md) | Release scope and evidence for each workflow |
| [Decision register](docs/architecture/DECISIONS.md) | Owner decisions, amendments and approval gates |
| [Inventory and BrickLink](docs/architecture/Inventory-and-BrickLink.md) | Stock custody, reservations, publication and reconciliation |
| [Legacy assessment](docs/architecture/Legacy-assessment.md) | Capabilities to evaluate before freezing the release scope |
| [Bricqer guide review](docs/architecture/Bricqer-guide-review.md) | Workflow references and their relevance to V2 |
| [Agent instructions](AGENTS.md) | Assignments, review and repository maintenance |
| [Native Codex acceptance](docs/verification/codex-native-acceptance.md) | Reproducible delegation and owner-pause exercise |
| [Changelog](CHANGELOG.md) | Changes proposed for each checkpoint |

## Reviewing a checkpoint

Link the candidate pull request, exact reviewed commit, local checks, independent review and hosted CI in the milestone issue. A reviewer who shares the maintainer's GitHub login can record the independent verdict against the exact diff. The owner can require a formal approval from a separate eligible GitHub account.

Before merging feature work, verify that GitHub enforces pull requests and the current `M0 repository checks` status check on `main`, with force pushes and deletion blocked. Use the standard public-repository runner for this workflow. After an authorized merge, fetch and compare local `main`, `origin/main` and GitHub's default branch. Keep the milestone and issue open until the owner accepts the checkpoint.

## Maintaining the tools

The tool manifest pins the scanner release and per-platform checksums. The workflow pins the checkout action to an upstream commit and selects a named runner OS. Review upstream releases, advisories and support dates at each milestone and monthly during active development. For upgrades, open a focused pull request with the new pins, a clean checkout run and a regression or failure check. If an upgrade fails, revert the tool change through review and rerun the checks.
