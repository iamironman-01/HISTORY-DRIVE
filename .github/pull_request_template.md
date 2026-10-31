## Summary

<!-- What does this change, and why? Link the issue it fixes, if there is one. -->

## Tests run

<!-- Paste the result lines of both commands. -->

```text
> dotnet build DriveMonitor.sln -nologo -v q -nodeReuse:false
(paste the Warning(s) and Error(s) lines)

> dotnet test tests/DriveMonitor.Tests/DriveMonitor.Tests.csproj --no-build -nologo
(paste the Passed! or Failed! line)
```

## Self-test result

<!--
Needed for changes to src/DriveMonitor.Core or src/DriveMonitor.Cli. Run
    ./scripts/Invoke-SelfTest.ps1 -Drive E
or, from an administrator terminal after a build,
    drivemon --self-test E: --report selftest.txt
Paste the RESULT line and every WARN or FAIL line. If you could not run it, say why; the
"Kernel trace self-test" jobs in CI run it on every push.
-->

```text
RESULT:
```

## Checklist

- [ ] The branch is named `fix/`, `feat/`, `docs/`, `ci/` or `build/` plus a topic.
- [ ] Each commit is one logical change, with a conventional subject (for example `fix(engine): ...`) and a
      body that explains why.
- [ ] The build has 0 warnings and 0 errors, and all tests pass.
- [ ] Tests were added or updated for changed behaviour in `DriveMonitor.Core`.
- [ ] README.md, `docs/` and CHANGELOG.md were updated if behaviour, options, messages or file locations
      changed.
- [ ] All CI jobs are green.
