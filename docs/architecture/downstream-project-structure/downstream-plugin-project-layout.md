# Downstream BepInEx plugin project layout (IL2CPP vs. Mono)

Part of the [downstream translation-project structure](downstream-project-structure.md). A downstream repo's
plugin project shape depends entirely on whether the target game is IL2CPP or Mono-backed Unity —
this is a hard fork in the `.csproj` shape, not a minor variation. Both branches are documented
here because a canonical shape doc must cover both, not just DragonHeir's case.

## IL2CPP shape — source of truth: `DragonHierOverLlm/DragonHeirPlugin/GamePlugin.csproj`

(Folder name is `DragonHeirPlugin/`; the `.csproj` file itself is named `GamePlugin.csproj` — the
project's `AssemblyName` is `FanslationStudio.EnglishPatch`, `RootNamespace` is `EnglishPatch`. File
naming under the plugin folder is not strictly consistent with the folder name in this repo — don't
assume the `.csproj` filename matches the containing folder name.)

Key shape elements:

- `TargetFramework net6.0`.
- `PackageReference BepInEx.Unity.IL2CPP` pinned to an exact prerelease version
  (`6.0.0-be.785` in DragonHeir), plus `BepInEx.Analyzers` and `BepInEx.PluginInfoProps`.
- `AllowUnsafeBlocks true`, `LangVersion latest`.
- `RestoreAdditionalProjectSources` includes the BepInEx and Samboy NuGet feeds
  (`nuget.bepinex.dev`, `nuget.samboy.dev`) alongside nuget.org.
- `<GameDir>` and `<ReleaseFolder>` MSBuild properties pointing at the real game's
  `BepInEx/plugins` folder and a release-staging folder — used by the `PostBuild` target.
- Interop references as plain `<Reference>` + `<HintPath>` entries pointing into
  `<GameDir>\BepInEx\interop\*.dll` — these DLLs only exist after BepInEx's IL2CPP
  unhollower/interop step has been run once against the real game install. `Il2CppSystem` and
  `Il2Cppmscorlib` are always among them; the rest (`Assembly-CSharp`, `Unity.TextMeshPro`,
  `UnityEngine.UI`, `UnityEngine.CoreModule`, `UnityEngine.AssetBundleModule`,
  `UnityEngine.TextRenderingModule`, `UnityEngine.UIModule`, `DOTween`, etc.) are whatever the
  plugin's patches actually touch — game-specific, not a fixed list to copy verbatim.
- **`Costura.Fody` + `Fody`**, used because BepInEx's IL2CPP SDK sets
  `CopyLocalLockFileAssemblies=false` project-wide, so an ordinary `PackageReference` (e.g.
  `System.Text.Encoding.CodePages` for GBK decode, `YamlDotNet` for parsing packaged YAML) never
  gets copied into the build output and can't reach the game's plugins folder unless it's embedded
  into the plugin DLL. DragonHeir overrides `CopyLocalLockFileAssemblies` back to `true` **for this
  project only** so package-reference DLLs land in `bin/` where Costura can find and embed them —
  this override + Costura pairing should be treated as a package, not adopted piecemeal. Per
  `new-translation-project`'s own existing guidance: only add Costura/Fody if a real dependency
  turns out to need embedding, don't add it preemptively.
- A `PostBuild` target that `XCOPY`s the built DLL into both `$(GameDir)` and
  `$(ReleaseFolder)`.

## Mono shape — verified against two real repos: `WanXiangOverLlm/EnglishPatch/EnglishPatch.csproj`
and `LegendOfMortalOverLlm/Plugin/Plugin.csproj`

Both confirmed by direct read. Shared shape, present in both:

- `TargetFramework netstandard2.1` (not `net6.0`).
- `PackageReference BepInEx.Core 5.*`, `BepInEx.Analyzers 1.*`, `BepInEx.PluginInfoProps 2.*` — no
  `BepInEx.Unity.IL2CPP` package at all.
- Same `RestoreAdditionalProjectSources` feeds as the IL2CPP shape (`nuget.bepinex.dev`,
  `nuget.samboy.dev`, alongside nuget.org).
- `AllowUnsafeBlocks true`, `LangVersion latest`.
- Plain `<Reference>` + `<HintPath>` entries pointing straight into
  `<Game>_Data\Managed\*.dll` for game/Unity assemblies — no interop-generation step needed, since
  Mono assemblies are already normal .NET IL the way BepInEx expects.
- A `<GameDir>` MSBuild property + `PostBuild XCOPY` target, same convention as the IL2CPP shape.
- No `Costura.Fody`/interop-embedding concern — neither repo uses it; Mono's `BepInEx.Core` SDK does
  not force `CopyLocalLockFileAssemblies=false` the way the IL2CPP SDK does, so ordinary
  `PackageReference` DLLs copy to the output normally.
- Plugin base class is `BaseUnityPlugin` (vs. IL2CPP's `BasePlugin`).

Where the two repos genuinely differ — treat these as **per-project choices**, not a single fixed
pattern to copy verbatim:

- **Unity assembly references.** WanXiang pins `PackageReference UnityEngine.Modules` (version
  `2022.1.0`) and gets `Assembly-CSharp`/`Unity.TextMeshPro`/`UnityEngine.UI` via plain `HintPath`
  into the installed game's `Managed` folder. LegendOfMortal skips the `UnityEngine.Modules`
  package entirely and references `UnityEngine.dll`/`UnityEngine.CoreModule.dll` directly via
  `HintPath` from the game's `Managed` folder too, alongside its own `Mortal.Core.dll`/
  `LeanLocalization.dll` (with `<Private>false</Private>` so they aren't copied into the output).
  Either approach works — `UnityEngine.Modules` is convenient when you want NuGet to resolve the
  matching Unity version's API surface without relying on whatever DLLs the install happens to
  ship; direct `HintPath` is simpler when you already know exactly which assemblies you need.
- **Harmony.** LegendOfMortal adds `PackageReference HarmonyX 2.7.0` (used for its runtime patches);
  WanXiang doesn't reference Harmony at all. Add it only if the plugin actually needs
  Harmony-style patching — it's not part of the baseline Mono shape.
- **PostBuild target.** WanXiang's just XCOPYs to `$(GameDir)`. LegendOfMortal's also copies to a
  `$(ReleaseFolder)` staging path and gates the whole target on `Condition="'$(CI)' != 'true'"` so
  it's skipped in CI. Both are reasonable; pick based on whether the new repo needs a release-zip
  workflow (see `FileOutputWorkflowTests.ZipRelease` pattern in `downstream-test-organization.md`)
  and/or has a CI pipeline that shouldn't try to XCOPY into a local game install.

## Deciding which branch a new repo needs

Per `new-translation-project`'s existing setup-question step: check whether `GameAssembly.dll` sits
next to the game's `.exe`. Present → IL2CPP. Absent, with `Assembly-CSharp.dll` directly under
`<Game>_Data\Managed` → Mono. This decision determines the entire plugin `.csproj` shape above; it
does not affect the tooling/`Files`/`Tests` project shapes, which are IL2CPP/Mono-agnostic.
