# Changelog

## M2 technical foundation in review

- Added the authenticated Vue Workbench, ASP.NET Core API and PostgreSQL reference register. Catalog and location commands retain their receipts across retries. Activity links recorded changes to the worker's result.
- Added a local TLS gateway, separate database roles, owner credential rotation, versioned migrations and an encrypted backup with release and data verification on restore. The runbook describes the qualification scope and outstanding production gates. [Foundation runbook](docs/verification/m2-foundation.md) · [M2 issue #6](https://github.com/abrianbaker80/BrickVault-V2/issues/6)
- Recorded the owner's authorization of M2 and direction to continue routine approved work through independent review and protected checkpoints. Major decisions return to the owner. [Decision register](docs/architecture/DECISIONS.md)

## M1 design and framework comparison accepted

- Added two interactive visual directions with fictional inventory, receiving, fulfillment and recovery records. Both native inventory registers share the same task tests. [M1 review](prototypes/m1/REVIEW.md)
- The owner accepted U01–U03 and selected Workbench navigation and visual design and Vue 3 with TypeScript for the frontend. [Accepted M1 comparison](https://github.com/abrianbaker80/BrickVault-V2/commit/d5a3264dcf3650bbf91fb98a132bb31b74c9fb92) · [M1 issue #4](https://github.com/abrianbaker80/BrickVault-V2/issues/4)

## M0 engineering checkpoint accepted

- Documented the architecture plan, release-scope questions and owner decision register for BrickVault V2.
- Defined a pull-request workflow with a required repository check, checksum-pinned tooling and redacted secret scanning.
- Added project-scoped Architect, Builder and Reviewer roles, a fictional test fixture and a repeatable procedure for verifying independent review, correction and an owner-approval pause.
- Witnessed the Insiders sidebar complete five native assignments, correct the fixture after independent review and pause before the separate README edit. Witnessed revision [b731a22](https://github.com/abrianbaker80/BrickVault-V2/commit/b731a225964b7d102243960f0308c9e248a98fb8).

The owner accepted the engineering checkpoint at [2d88409](https://github.com/abrianbaker80/BrickVault-V2/commit/2d88409b5fb556316888d1282c08968ba5619c70). The checkpoint issue records G03’s post-merge synchronization check. The owner subsequently authorized M1. [M0 issue #1](https://github.com/abrianbaker80/BrickVault-V2/issues/1)
