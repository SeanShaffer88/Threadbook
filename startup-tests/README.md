# Desktop startup policy tests

This console host links the desktop StartupStorage source and uses the real storage module. It has no WPF dependency and creates only a supplied new synthetic fixture directory.

```powershell
dotnet run --project startup-tests/Threadbook.StartupTests.csproj -c Release -- '<new-absolute-fixture-directory>'
```

Checks cover default selection without opening it, marker resolution independent of CWD, explicit priority, invalid markers, real portable writes, Data and application-ancestor junction rejection, exclusive-lock failure, and Windows read-only file denial without default fallback. Marker symbolic-link rejection runs when fixture creation is permitted; otherwise the test reports Unsupported. Directory ACL and installed lifecycle checks are separate. Root MIT license applies.
