# Third-party components

VPN Health Monitor itself is MIT-licensed (see [LICENSE](LICENSE)).

The shipped application (`VpnHealthMonitor.csproj`) has **zero NuGet
dependencies** — it builds against the .NET 8 SDK and the Windows Desktop
(WPF) runtime only. Nothing beyond the .NET/Windows Desktop Runtime license
terms applies to a built binary.

## Dev-only — not part of the shipped binary

The test project `VpnHealthMonitor.Tests.csproj` (xUnit) references three
packages. They run at test time only and are never bundled into the
application's output:

| Component | License |
|---|---|
| [xunit](https://github.com/xunit/xunit) | MIT |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | MIT |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |

If a future change adds a runtime NuGet dependency to the main project, this
file must be updated to match — check `VpnHealthMonitor.csproj`'s
`<PackageReference>` entries against this table before publishing a release.
