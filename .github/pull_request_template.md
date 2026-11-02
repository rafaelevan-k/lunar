## Task

<!-- One task ID per change set, for example FE-07 or BE-10. -->
Task ID:

## What changed and why

## Verification

<!-- Paste real command results. Write NOT_RUN for anything that did not run, and say why. -->
- [ ] `./scripts/build.ps1` (locked restore, Release build, formatting)
- [ ] `./scripts/test.ps1 -Suite Gate`
- [ ] Windows-session tests (`-Suite Hardware`) or manual Windows check, with the result:

## Checklist

- [ ] Only the assigned task is changed; no speculative work on later tasks
- [ ] Layer rules respected; no OS, file or settings calls in ViewModels
- [ ] Contract, settings schema or security-boundary changes have an accepted ADR and updated docs
- [ ] No secrets, personal documents, captured media or private logs committed
- [ ] Task status, evidence and `CHANGELOG.md` updated; failures not rewritten or hidden
- [ ] New dependencies reviewed for license and recorded in `THIRD_PARTY_NOTICES.md`
