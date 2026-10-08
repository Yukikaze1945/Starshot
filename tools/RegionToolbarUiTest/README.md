# Region toolbar validation

Run `Run.ps1` from PowerShell, optionally with `-Configuration Release`. It
requires .NET 10 on PATH; use `-DotNetPath <dotnet executable>` for a separate SDK.
Use `-PublishedApp <publish directory>` to validate the actual trimmed application
in a copied test host without rebuilding or modifying the published payload.
For a trimmed publish, also pass `-ReferenceApp <matching pre-trim Starshot.dll>`:
the compiler requires reference-compatible assemblies, while the test process loads
the trimmed runtime assemblies. The build output from the same publish is suitable.
The harness is an independent test application loading an isolated Starshot assembly and its PRI.
It creates the actual XAML toolbar in a hidden window using the real resource dictionary
and metadata provider. Its application
overrides launch, so no single-instance registration, tray, configuration, WGC capture,
browser, installed application or desktop input is involved. It tests semantic actions,
actual button invocation, parameter values, group menus, outside-click dismissal and
the Escape hierarchy. All pixels remain in memory; only logs are written.

`tools/RegionToolbarTest` tests the renderer-independent layout, action inventory,
shortcuts, group memory, negative coordinates, mixed DPI and narrow monitor overflow.
`tools/LightweightCaptureTest` tests the real GDI renderer with offscreen synthetic
windows. Run its normal mode; no `--memory` stress run is needed for this change.

Neither this test suite nor its build updates the installed application or publishes
anything. The GDI suite deliberately attempts one cross-thread DestroyWindow and
checks that the original thread can retry; its expected warning is not a failed test.

`-HdrDiagnostics` runs 15 complete 4K FP16 analysis open/close cycles plus cancellation,
exception, selection-change and optional-view cleanup checks. It records weak references,
process/GC/LOH/KMT memory and readback/CPU/dispatcher timings. Only this diagnostic process
performs a final explicit GC after all operations stop; the normal application does not.
`Summarize-HdrDiagnostics.py <report root>` summarizes saved CSV/JSON without accessing
Starshot; plotting requires Matplotlib in the Python environment or the isolated
`build/hdr-analysis-plot-packages` directory.
