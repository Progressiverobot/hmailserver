## Summary

<!-- What does this PR change and why? -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactoring / internal
- [ ] Documentation
- [ ] Build / installer

## Checklist

- [ ] Builds warning-free (`/WX`) with `build/build.ps1`
- [ ] Regression suite passes (898/898) — see IMPLEMENTATION-NOTES.md for the environment recipe
- [ ] New behavior covered by regression tests
- [ ] SQL uses parameterised queries only
- [ ] DB schema changes include scripts for MySQL, MS SQL and PostgreSQL in `source/DBScripts/`
- [ ] New settings exposed via COM API or `hMailServer.INI` pattern as appropriate
