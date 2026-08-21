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
- [ ] Regression suite passes with no failures (`build/run-tests.ps1`; run
      `build/preflight-tests.ps1` first, or a stale bench will fail tests that
      have nothing to do with your change)
- [ ] New behavior covered by regression tests
- [ ] SQL uses parameterised queries only
- [ ] DB schema changes include scripts for MySQL, MS SQL and PostgreSQL in `source/DBScripts/`
- [ ] New settings exposed via COM API or `hMailServer.INI` pattern as appropriate
