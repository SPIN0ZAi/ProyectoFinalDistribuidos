# Contributing Guide — ProyectoFinalDistribuidos

## Team
- **@SPIN0ZAi** (Soulaimane Ben Louaret) — Core & Server Lead
- **@eliasaoufi** (Elías Aoufi) — Client & UI Lead
- **@Manuvelf2** — Messaging & DevOps Lead

## Branching Strategy (GitHub Flow)
- `main` is the protected integration branch. Never commit directly to `main`.
- Always branch from up-to-date `main` using the issue ID:
  - `feat/dice-XXX-short-description`
  - `fix/dice-XXX-short-description`
  - `test/dice-XXX-short-description`
  - `docs/dice-XXX-short-description`
  - `chore/dice-XXX-short-description`

```bash
git checkout main
git pull origin main
git checkout -b feat/dice-003-protobuf-contract
```

## Pull Request & Code Review Rules
1. Link the issue in the PR description using `Closes #<number>`.
2. Wait for **CI (`Build & Test (.NET 10)`)** to run on your PR.
   > **Note during M0/M1:** Full-solution builds will fail until the initial starter placeholders (`contracts/game.proto`, `GameCoordinator`, `GameGrpcService`, `GrpcGameClient`, `ConsoleGame`) are completed. Once M1 compiles cleanly, CI must be green on every PR.
3. Obtain at least **1 peer approval** from another team member before merging into `main`.
4. Delete your feature branch after merging.

## Code Quality Rules
- Zero compiler warnings (`<Nullable>enable</Nullable>`).
- Never manually edit generated Protobuf files under `obj/`.
- Keep `Game.Core` free of gRPC and RabbitMQ dependencies.
- Never commit `bin/`, `obj/`, or secrets.
