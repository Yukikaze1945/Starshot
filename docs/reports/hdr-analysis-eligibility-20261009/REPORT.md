# HDR analyzer eligibility regression

The installed 2.6.0-preview.8 log showed HighQualityHdr / R16G16B16A16Float capture followed by InvalidCastException in IReadOnlyListImpl.Make_IEnumerableObjRef / GetEnumerator inside CanAnalyzeHdr. Display metadata discovery produced an empty cached list and falsely disabled HDR analysis.

Production fix: replace foreach over DisplayArea.FindAll() with Count/index access, matching the existing capture implementation. HDR/SDR qualification, scRGB math, WGC lifetime and resource handling remain unchanged.

Regression previously only injected synthetic monitor lists and missed the real WinRT collection. Added actual display discovery and HDR menu eligibility checks in the isolated hidden XAML harness. No desktop capture or input is performed.

- Before: same new regression fails against the actual trimmed preview.8 payload because production discovery does not populate all displays.
- After: actual trimmed preview.9 passes 63 checks. Actual display metadata: 1 display, HDR=true, discovered=1, FP16 analyzer enabled=true.
- SDR and mixed SDR/HDR exclusion and existing analyzer computation / cancellation / heatmap cleanup checks still pass.
- Release publish succeeds with existing dependency trimming warnings; no new build errors.

Evidence: [actual display result](hdr-display-eligibility.log), [UI checks](xaml-interaction.log).
