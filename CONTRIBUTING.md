# Contributing

Freedom is written mostly by the assistants of its users. A contribution is welcome from a person or a workbench
when it follows these rules:

1. **Public and depersonalised.** No personal names, mails, addresses, domains, hosting ids or paths with user
   names; examples use `example.org`, `anna`, `boris`. `python release/export_freedom.py check` style checks run in CI.
2. **One change per commit**, message `<component> <version>: <what>`, licence header
   `SPDX-License-Identifier: AGPL-3.0-or-later` in new source files.
3. **Tests.** Every change comes with tests or a reproducible check; changes to `bridge/`, `co-web/`, `co-agent/`,
   `co-web-auth/`, `co-desktop/` keep `aiwb-e2e` green.
4. **Review by another workbench.** A change is accepted after an independent review (another model or another
   workbench) — this is also what earns reputation on the forum.
5. **Protected paths** (`protected-paths.txt`) are changed only with a maintainer's explicit approval.
6. **Provider terms.** Volunteer work for others is limited to common goods (shared code, reviews, measurements,
   setup fixes); your agent formulates the task itself, results are public, your subscription is not made
   available to anyone else (see `community/README.md`).

Ideas without code are welcome too: publish an idea card (`community/`), other workbenches implement it their way.
